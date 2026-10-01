import CoreGraphics
import Foundation
import Observation

enum MessageInputSource {
    case overlay
    case history
}

@MainActor
@Observable
final class AppModel {
    @ObservationIgnored let characterStunState = CharacterStunState()
    @ObservationIgnored lazy var characterImpactAudio = CharacterImpactAudio()
    var preferences: AppPreferences
    var overlayVisibility: OverlayVisibility
    var overlayVisible: Bool { overlayVisibility.isVisible }
    var presence: PresenceState = .online
    var nickname: String
    private(set) var confirmedNickname: String?
    var selectedCharacterID: String
    private(set) var pendingCharacterID: String?
    private(set) var equippedBubbleStyleID: String?
    private(set) var equippedThrowableID: String?
    private(set) var activeEntitlementKeys: Set<String> = []
    private(set) var snapshotActiveEntitlementKeys: Set<String> = []
    private(set) var cosmeticEquipmentRequests: [CommerceProductKind: CosmeticEquipmentRequest] = [:]
    private let commerce: AppCommerceProducts
    var commerceProducts: [CommerceProductState] { commerce.commerceProducts }
    private let messages = AppMessageState()
    var draft: String { get { messages.draft } set { messages.draft = newValue } }
    var messageLedger: MessageLedger { messages.messageLedger }
    var messageOutbox: MessageOutbox { messages.messageOutbox }
    var bubbleLedger: ActiveBubbleLedger { messages.bubbleLedger }
    var availableScreens: [OverlayScreenOption] = []
    var activeSettingsPage: SettingsPage = .profile
    var connectionState: BackendConnectionState = .idle
    var authenticationRequired = false
    var accountOperationInProgress = false
    var activeRoomTransportConnected: Bool { realtime.activeRoomTransportConnected }
    var rooms: [Room] = []
    var drawEvents: [DrawEvent] = []
    var drawEventsRoomID: UUID?
    var drawHistoryRoomID: UUID?
    var drawHistoryEvents: [DrawEvent] = []
    var drawHistoryLoading = false
    var drawHistoryError: String?
    var drawOperationInProgress = false
    var visibleDrawEvents: [DrawEvent] {
        guard let roomID = activeRoom?.id, drawEventsRoomID == roomID else { return [] }
        return drawEvents.filter {
            $0.iconVisible && $0.closedAt == nil && ($0.iconExpiryDate ?? .distantPast) > .now
        }
    }
    var hasProfile = false
    let treeMovement = TreeMovementState()
    var currentUserID: UUID? {
        didSet {
            if oldValue != currentUserID {
                treeMovement.reset()
                realtime.clearOverlaySuppression()
                drawHistoryRoomID = nil
                drawHistoryEvents = []
                drawHistoryLoading = false
                drawHistoryError = nil
            }
        }
    }
    var errorMessage: String?
    var historySendError: String?
    var globalShortcutStatuses: [GlobalShortcutAction: GlobalShortcutStatus] = [:]
    private(set) var typingInputSource: MessageInputSource?
    private var successFeedback = SuccessFeedbackState()
    var successMessage: String? { successFeedback.message }
    var successMessageGeneration: Int { successFeedback.generation }
    var isWorking = false
    var groupOperation: GroupOperation = .idle
    var newRoomName = ""
    var inviteCode = ""
    var lastCreatedInviteCode: String?
    var launchAtLogin: Bool
    var unreadCounts: [UUID: Int] { messages.unreadCounts }
    private let realtime = RoomPresenceState()

    init(
        preferences: AppPreferences,
        commerceProducts: [CommerceProduct] = CommerceCatalog.products
    ) {
        self.preferences = preferences
        self.overlayVisibility = OverlayVisibility(isVisible: preferences.overlayVisible)
        self.nickname = preferences.nickname
        self.confirmedNickname = preferences.onboardingComplete
            ? ProfileValidator.normalizedNickname(preferences.nickname)
            : nil
        self.selectedCharacterID = PixelCharacterCatalog.canonicalID(for: preferences.selectedCharacterID)
        self.pendingCharacterID = nil
        self.equippedBubbleStyleID = nil
        self.equippedThrowableID = nil
        self.launchAtLogin = preferences.launchAtLogin
        self.commerce = AppCommerceProducts(products: commerceProducts)
    }

    func setOverlayVisibility(_ visibility: OverlayVisibility) {
        overlayVisibility = visibility
        preferences.overlayVisible = visibility.isVisible
    }

    var localOverlaySuppressed: Bool { !preferences.overlayVisible || preferences.quietModeEnabled }

    func updateOverlaySuppression(roomID: UUID, userID: UUID, suppressed: Bool) {
        guard rooms.contains(where: { $0.id == roomID && $0.members.contains(where: { $0.userID == userID }) }) else { return }
        realtime.updateOverlaySuppression(roomID: roomID, userID: userID, suppressed: suppressed)
    }

    var canSubmitDraft: Bool {
        groupOperation == .idle && !isWorking && activeRoom != nil
            && MessageValidator.isValid(MessageValidator.normalized(draft))
    }

    /// Only the input that last received an edit may end that typing session.
    /// A delayed focus-loss callback from the other window cannot cancel it.
    @discardableResult
    func updateTypingInput(active: Bool, source: MessageInputSource) -> Bool {
        if active {
            guard groupOperation == .idle else { return false }
            typingInputSource = source
            return true
        }
        guard typingInputSource == source else { return false }
        typingInputSource = nil
        return true
    }

    func resetTypingInput() { typingInputSource = nil }

    func acceptDraft() -> String? {
        guard canSubmitDraft else { return nil }
        let normalized = MessageValidator.normalized(draft)
        guard MessageValidator.isValid(normalized) else { return nil }
        draft = ""
        return normalized
    }

    var activeRoom: Room? {
        if let activeRoomID = preferences.activeRoomID,
           let match = rooms.first(where: { $0.id == activeRoomID }) {
            return match
        }
        return rooms.first
    }

    var realtimeActiveRoomID: UUID? {
        if case .switching(let roomID) = groupOperation { return roomID }
        return resolvedActiveRoomID(in: rooms)
    }

    func resolvedActiveRoomID(in availableRooms: [Room]) -> UUID? {
        if let preferredRoomID = preferences.activeRoomID,
           availableRooms.contains(where: { $0.id == preferredRoomID }) {
            return preferredRoomID
        }
        return availableRooms.first?.id
    }

    var groupMutationsDisabled: Bool {
        isWorking || groupOperation.blocksMutations
    }

    var normalizedNicknameDraft: String {
        ProfileValidator.normalizedNickname(nickname)
    }

    var nicknameDraftIsValid: Bool {
        ProfileValidator.isValidNickname(nickname)
    }

    var hasNicknameChanges: Bool {
        guard let confirmedNickname else { return false }
        return normalizedNicknameDraft != confirmedNickname
    }

    func presentSuccess(_ message: String) {
        successFeedback.present(message)
    }

    func dismissSuccess(generation: Int? = nil) {
        successFeedback.dismiss(generation: generation)
    }

    func apply(snapshot: BackendSnapshot, currentUserID: UUID?) {
        self.currentUserID = currentUserID
        authenticationRequired = false
        activeEntitlementKeys = snapshot.activeEntitlementKeys
        snapshotActiveEntitlementKeys = snapshot.activeEntitlementKeys
        hasProfile = snapshot.profile != nil
        // Fold every profile before projection so response ordering cannot undo a confirmed revision.
        if let profile = snapshot.profile {
            treeMovement.accept(userID: profile.id, paused: profile.treeMovementPaused,
                                revision: profile.treeMovementRevision)
        }
        for room in snapshot.rooms {
            for member in room.members {
                treeMovement.accept(userID: member.userID, paused: member.treeMovementPaused,
                                    revision: member.treeMovementRevision)
            }
        }
        let updatedRooms = realtime.reconcile(snapshot.rooms.map { room in
            var room = room
            room.members = room.members.map { member in
                var member = member
                if let value = treeMovement.confirmed[member.userID] {
                    member.treeMovementPaused = value.paused
                    member.treeMovementRevision = value.revision
                }
                return member
            }
            return room
        })
        rooms = updatedRooms
        messages.retain(roomIDs: Set(updatedRooms.map(\.id)))
        if let profile = snapshot.profile {
            let shouldAdoptNickname = confirmedNickname.map {
                normalizedNicknameDraft == $0
            } ?? true
            confirmedNickname = ProfileValidator.normalizedNickname(profile.nickname)
            if shouldAdoptNickname { nickname = profile.nickname }
            preferences.nickname = profile.nickname
            selectedCharacterID = PixelCharacterCatalog.canonicalID(for: profile.characterID)
            preferences.selectedCharacterID = selectedCharacterID
            equippedBubbleStyleID = profile.equippedBubbleStyleID
            equippedThrowableID = profile.equippedThrowableID
        } else {
            confirmedNickname = nil
            equippedBubbleStyleID = nil
            equippedThrowableID = nil
        }
        enforceSelectableCurrentCharacter()
        enforceOwnedCosmetics()
        preferences.activeRoomID = resolvedActiveRoomID(in: rooms)
        preferences.onboardingComplete = snapshot.profile != nil && !rooms.isEmpty
    }

    func apply(profile: Profile) {
        guard currentUserID == profile.id else { return }
        hasProfile = true
        applyTreeMovement(profile: profile)
        let shouldAdoptNickname = confirmedNickname.map {
            normalizedNicknameDraft == $0
        } ?? true
        confirmedNickname = ProfileValidator.normalizedNickname(profile.nickname)
        if shouldAdoptNickname { nickname = profile.nickname }
        preferences.nickname = profile.nickname
        selectedCharacterID = PixelCharacterCatalog.canonicalID(for: profile.characterID)
        preferences.selectedCharacterID = selectedCharacterID
        equippedBubbleStyleID = profile.equippedBubbleStyleID
        equippedThrowableID = profile.equippedThrowableID
        for roomIndex in rooms.indices {
            guard let memberIndex = rooms[roomIndex].members.firstIndex(where: {
                $0.userID == profile.id
            }) else { continue }
            rooms[roomIndex].members[memberIndex].nickname = profile.nickname
            rooms[roomIndex].members[memberIndex].characterID = selectedCharacterID
            rooms[roomIndex].members[memberIndex].equippedBubbleStyleID = equippedBubbleStyleID
        }
        enforceSelectableCurrentCharacter()
        enforceOwnedCosmetics()
    }

    func applyTreeMovement(profile: Profile) {
        let value = treeMovement.accept(userID: profile.id, paused: profile.treeMovementPaused,
                                        revision: profile.treeMovementRevision)
        for roomIndex in rooms.indices {
            for memberIndex in rooms[roomIndex].members.indices
                where rooms[roomIndex].members[memberIndex].userID == profile.id {
                rooms[roomIndex].members[memberIndex].treeMovementPaused = value.paused
                rooms[roomIndex].members[memberIndex].treeMovementRevision = value.revision
            }
        }
    }

    var selectableCharacters: [PixelCharacterDefinition] {
        PixelCharacterCatalog.selectableDefinitions(entitlementKeys: activeEntitlementKeys)
    }

    func isCharacterSelectable(_ characterID: String) -> Bool {
        PixelCharacterCatalog.canSelect(characterID, entitlementKeys: activeEntitlementKeys)
    }

    func apply(commerceState: CommerceState) {
        guard commerce.apply(commerceState) else { return }
        if commerceState.entitlementStatus == "active" {
            activeEntitlementKeys.insert(commerceState.product.entitlementKey)
            if commerceState.isEquipped {
                switch commerceState.product.kind {
                case .bubble:
                    equippedBubbleStyleID = commerceState.product.catalogItemID
                case .throwable:
                    equippedThrowableID = commerceState.product.catalogItemID
                case .character:
                    break
                }
            }
        } else {
            activeEntitlementKeys.remove(commerceState.product.entitlementKey)
            enforceSelectableCurrentCharacter()
            enforceOwnedCosmetics()
        }
    }

    func apply(commerceStates: [CommerceState]) {
        for state in commerceStates { apply(commerceState: state) }
        func equipment(kind: CommerceProductKind, currentID: String?) -> String? {
            if let equipped = commerceStates.first(where: {
                $0.product.kind == kind && $0.entitlementStatus == "active" && $0.isEquipped
            }) {
                return equipped.product.catalogItemID
            }
            // Absence from a partial catalog says nothing about an existing profile selection.
            let selectionWasReturned = commerceStates.contains {
                $0.product.kind == kind && $0.product.catalogItemID == currentID
            }
            return selectionWasReturned ? nil : currentID
        }
        equippedBubbleStyleID = equipment(kind: .bubble, currentID: equippedBubbleStyleID)
        equippedThrowableID = equipment(kind: .throwable, currentID: equippedThrowableID)
        enforceOwnedCosmetics()
    }

    func applyStoreCatalog(_ states: [CommerceState], usesAppStore: Bool) {
        apply(commerceStates: states)
        let returnedIDs = Set(states.map { $0.product.id })
        for state in commerceProducts {
            if !returnedIDs.contains(state.id) {
                // A missing sale offer must not revoke a separately verified entitlement.
                commerce.setCommercePurchaseState(
                    activeEntitlementKeys.contains(state.product.entitlementKey) ? .owned : .unavailable,
                    productID: state.id
                )
            } else if usesAppStore && state.purchaseState != .owned {
                commerce.setCommercePurchaseState(.available, productID: state.id)
            }
        }
    }

    func failStoreCatalogLoading(productIDs: [String]) {
        for id in productIDs {
            guard let state = commerceProduct(id: id) else { continue }
            commerce.setCommercePurchaseState(
                activeEntitlementKeys.contains(state.product.entitlementKey)
                    ? .owned : .error(L10n.text("commerce.error.catalog_unavailable")), productID: id
            )
        }
    }

    func commerceProduct(id: String) -> CommerceProductState? {
        commerceProducts.first { $0.id == id }
    }

    func ownedProfileCosmeticProducts(for kind: CommerceProductKind) -> [CommerceProduct] {
        guard kind == .bubble || kind == .throwable else { return [] }
        return CommerceCatalog.cosmeticProducts
            .filter {
                $0.kind == kind && snapshotActiveEntitlementKeys.contains($0.entitlementKey)
            }
            .sorted { $0.sortOrder < $1.sortOrder }
    }

    func equippedCosmeticID(for kind: CommerceProductKind) -> String? {
        switch kind {
        case .bubble: equippedBubbleStyleID
        case .throwable: equippedThrowableID
        case .character: nil
        }
    }

    @discardableResult
    func beginCosmeticEquipmentRequest(
        kind: CommerceProductKind,
        catalogItemID: String?
    ) -> Bool {
        guard kind != .character, cosmeticEquipmentRequests[kind] == nil else { return false }
        cosmeticEquipmentRequests[kind] = CosmeticEquipmentRequest(
            kind: kind,
            catalogItemID: catalogItemID
        )
        return true
    }

    func endCosmeticEquipmentRequest(kind: CommerceProductKind) {
        cosmeticEquipmentRequests.removeValue(forKey: kind)
    }

    func cosmeticEquipmentRequest(for kind: CommerceProductKind) -> CosmeticEquipmentRequest? {
        cosmeticEquipmentRequests[kind]
    }

    @discardableResult
    func beginCharacterEquipmentRequest(characterID: String) -> Bool {
        let canonicalID = PixelCharacterCatalog.canonicalID(for: characterID)
        guard pendingCharacterID == nil,
              selectedCharacterID != canonicalID,
              isCharacterSelectable(canonicalID)
        else { return false }
        pendingCharacterID = canonicalID
        return true
    }

    func endCharacterEquipmentRequest() {
        pendingCharacterID = nil
    }

    var snapshotActiveEntitlements: Set<String> {
        snapshotActiveEntitlementKeys
    }

    func setCommerceWorking(_ isWorking: Bool, productID: String) { commerce.setCommerceWorking(isWorking, productID: productID) }
    func setCommercePurchaseState(_ state: CommercePurchaseState, productID: String) { commerce.setCommercePurchaseState(state, productID: productID) }
    func beginCommercePriceLoading() { commerce.beginCommercePriceLoading() }
    func failCommercePriceLoading() { commerce.failCommercePriceLoading() }
    func setCommerceStorefrontMetadata(_ metadata: [String: StorefrontProductMetadata]) {
        commerce.setCommerceStorefrontMetadata(metadata)
    }

    private func enforceSelectableCurrentCharacter() {
        guard !isCharacterSelectable(selectedCharacterID) else { return }
        selectedCharacterID = PixelCharacterCatalog.pixelHamsterID
        preferences.selectedCharacterID = selectedCharacterID
        guard let currentUserID else { return }
        for roomIndex in rooms.indices {
            guard let memberIndex = rooms[roomIndex].members.firstIndex(where: {
                $0.userID == currentUserID
            }) else { continue }
            rooms[roomIndex].members[memberIndex].characterID = PixelCharacterCatalog.pixelHamsterID
        }
    }

    private func enforceOwnedCosmetics() {
        if let equippedBubbleStyleID,
           !CommerceCatalog.products.contains(where: {
               $0.kind == .bubble
                   && $0.catalogItemID == equippedBubbleStyleID
                   && activeEntitlementKeys.contains($0.entitlementKey)
           }) {
            self.equippedBubbleStyleID = nil
        }
        if let equippedThrowableID,
           !CommerceCatalog.products.contains(where: {
               $0.kind == .throwable
                   && $0.catalogItemID == equippedThrowableID
                   && activeEntitlementKeys.contains($0.entitlementKey)
           }) {
            self.equippedThrowableID = nil
        }
        guard let currentUserID else { return }
        for roomIndex in rooms.indices {
            guard let memberIndex = rooms[roomIndex].members.firstIndex(where: {
                $0.userID == currentUserID
            }) else { continue }
            rooms[roomIndex].members[memberIndex].equippedBubbleStyleID = equippedBubbleStyleID
        }
        commerce.updateEquipment(characterID: selectedCharacterID, bubbleID: equippedBubbleStyleID,
                                 throwableID: equippedThrowableID)
    }

    var effectiveLocalPresence: PresenceState {
        // Snapshot/message reconciliation may still be running after the
        // Realtime transport has already recovered. Presence is published on
        // that transport, so do not leave only the local character gray while
        // peers can already see it online.
        if activeRoomRealtimeAvailable {
            return presence
        }
        return switch connectionState {
        case .connecting:
            .reconnecting
        case .idle, .failed, .online:
            .offline
        }
    }

    var activeRoomRealtimeAvailable: Bool {
        activeRoomTransportConnected || connectionState == .online
    }

    var pixelWorldMembers: [PixelWorldMember] {
        OverlayMemberProjection.members(room: activeRoom, currentUserID: currentUserID,
                                        localPresence: effectiveLocalPresence,
                                        showsOffline: preferences.showOfflineMembers, state: realtime,
                                        quietModeEnabled: preferences.quietModeEnabled,
                                        forcedVisibleUserIDs: Set(visibleDrawEvents.map(\.creatorID)),
                                        localOverlaySuppressed: localOverlaySuppressed)
    }

    var activeBubbles: [ActiveBubble] { preferences.quietModeEnabled ? [] : bubbleLedger.bubbles }

    var totalUnreadCount: Int { messages.totalUnreadCount }
    var activeRoomUnreadCount: Int { activeRoom.map { messages.unreadCount(in: $0.id) } ?? 0 }
    func unreadCount(in roomID: UUID) -> Int { messages.unreadCount(in: roomID) }
    func markRoomRead(_ roomID: UUID) { messages.markRoomRead(roomID) }
    func incrementUnread(in roomID: UUID) { messages.incrementUnread(in: roomID) }

    func updatePresence(roomID: UUID, userID: UUID, state: PresenceState) {
        realtime.updatePresence(roomID: roomID, userID: userID, state: state, rooms: &rooms)
    }

    func updateTyping(roomID: UUID, userID: UUID, active: Bool) {
        realtime.updateTyping(roomID: roomID, userID: userID, active: active, rooms: &rooms)
    }

    func setActiveRoomRealtimeConnected(_ connected: Bool) {
        if activeRoomTransportConnected != connected {
            characterStunState.reset()
            characterImpactAudio.stopAll()
        }
        realtime.setConnected(connected, activeRoomID: activeRoom?.id, currentUserID: currentUserID, rooms: &rooms)
    }

    func stageMessage(id: UUID, roomID: UUID, senderID: UUID, body: String,
                      revealBubble: Bool = true, now: Date = .now) {
        messages.stageMessage(id: id, roomID: roomID, senderID: senderID, body: body,
                              revealBubble: revealBubble, now: now, activeRoomID: activeRoom?.id,
                              equippedBubbleStyleID: equippedBubbleStyleID)
    }

    @discardableResult
    func confirmMessage(_ message: ChatMessage, revealBubble: Bool = true) -> Bool {
        messages.confirmMessage(message, revealBubble: revealBubble, activeRoomID: activeRoom?.id)
    }

    func failMessage(id: UUID, roomID: UUID) -> OutgoingMessage? {
        messages.failMessage(id: id, roomID: roomID)
    }

    func replaceMessages(roomID: UUID, with values: [ChatMessage]) {
        messages.replaceMessages(roomID: roomID, with: values)
    }

    func removeMessage(id: UUID, roomID: UUID) { messages.removeMessage(id: id, roomID: roomID) }
    func clearBubbles() { messages.clearBubbles() }
    func dismissExpiredBubbles(at date: Date = .now) { messages.dismissExpiredBubbles(at: date) }

}
