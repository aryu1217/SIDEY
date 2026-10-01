import Foundation

@MainActor
enum OverlayMemberProjection {
    static func members(room: Room?, currentUserID: UUID?, localPresence: PresenceState,
                        showsOffline: Bool, state: RoomPresenceState, quietModeEnabled: Bool = false,
                        forcedVisibleUserIDs: Set<UUID> = [], localOverlaySuppressed: Bool = false) -> [PixelWorldMember] {
        guard let activeRoom = room else { return [] }
        return activeRoom.members.compactMap { member in
            let isCurrentUser = member.userID == currentUserID
            let isTyping = !quietModeEnabled && state.isTyping(roomID: activeRoom.id, userID: member.userID)
            let baseState = isCurrentUser
                ? localPresence
                : (state.baseState(roomID: activeRoom.id, userID: member.userID) ?? (member.presence == .typing ? .online : member.presence))
            guard isCurrentUser || showsOffline || baseState != .offline
                    || forcedVisibleUserIDs.contains(member.userID) else { return nil }
            return PixelWorldMember(
                id: member.userID,
                nickname: ProfileValidator.displayNickname(member.nickname),
                characterID: PixelCharacterCatalog.canonicalID(for: member.characterID),
                presence: baseState,
                isTyping: isTyping,
                isCurrentUser: isCurrentUser,
                equippedBubbleStyleID: member.equippedBubbleStyleID,
                treeMovementPaused: member.treeMovementPaused,
                overlaySuppressed: isCurrentUser ? localOverlaySuppressed
                    : (baseState != .offline && baseState != .reconnecting
                       && state.isOverlaySuppressed(roomID: activeRoom.id, userID: member.userID))
            )
        }
    }

}
