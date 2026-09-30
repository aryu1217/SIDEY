import Foundation
import Observation

typealias MessageHistoryPageLoader = (
    _ roomID: UUID,
    _ cursor: MessageHistoryCursor?,
    _ pageSize: Int
) async throws -> MessageHistoryPage

enum MessageHistoryInitialState: Equatable {
    case idle
    case loading
    case loaded
    case failed(String)
}

enum MessageHistoryOlderState: Equatable {
    case idle
    case loading
    case failed(String)
    case exhausted
}

@MainActor
@Observable
final class MessageHistoryStore {
    static let defaultPageSize = 50

    private(set) var roomID: UUID?
    private(set) var messages: [ChatMessage] = []
    private(set) var initialState: MessageHistoryInitialState = .idle
    private(set) var olderState: MessageHistoryOlderState = .idle

    private let pageSize: Int
    private let loadPage: MessageHistoryPageLoader
    private var nextCursor: MessageHistoryCursor?
    private var requestTask: Task<Void, Never>?
    private var generation: UInt64 = 0
    private var isActive = false
    private var deletedMessageIDs: Set<UUID> = []

    convenience init(loadPage: @escaping MessageHistoryPageLoader) {
        self.init(pageSize: Self.defaultPageSize, loadPage: loadPage)
    }

    init(
        pageSize: Int,
        loadPage: @escaping MessageHistoryPageLoader
    ) {
        self.pageSize = min(max(pageSize, 1), 50)
        self.loadPage = loadPage
    }

    func activate(roomID: UUID?) {
        isActive = true
        transition(to: roomID)
    }

    func roomDidChange(to roomID: UUID?) {
        guard isActive else { return }
        transition(to: roomID)
    }

    func deactivate() {
        isActive = false
        cancelAndReset(roomID: nil)
    }

    func retryInitial() {
        guard isActive, let roomID, case .failed = initialState else { return }
        startInitialLoad(roomID: roomID)
    }

    func loadNextPage() {
        guard isActive,
              let roomID,
              initialState == .loaded,
              olderState == .idle,
              let cursor = nextCursor
        else { return }

        olderState = .loading
        let requestGeneration = generation
        let loader = loadPage
        let pageSize = pageSize
        requestTask = Task { [weak self] in
            do {
                let page = try await loader(roomID, cursor, pageSize)
                guard !Task.isCancelled,
                      let self,
                      self.isCurrent(roomID: roomID, generation: requestGeneration)
                else { return }
                self.messages = MessageHistoryMerge.mergeConfirmed(
                    self.messages,
                    with: page.messages.filter { !self.deletedMessageIDs.contains($0.id) },
                    roomID: roomID
                )
                self.nextCursor = page.nextCursor
                self.olderState = page.nextCursor == nil || page.nextCursor == cursor
                    ? .exhausted
                    : .idle
                self.requestTask = nil
            } catch is CancellationError {
                return
            } catch {
                guard let self,
                      self.isCurrent(roomID: roomID, generation: requestGeneration)
                else { return }
                self.olderState = .failed(
                    SideyBackendError.normalized(error).localizedDescription
                )
                self.requestTask = nil
            }
        }
    }

    func retryNextPage() {
        guard case .failed = olderState else { return }
        olderState = .idle
        loadNextPage()
    }

    func remove(messageID: UUID, roomID: UUID) {
        guard isActive, self.roomID == roomID else { return }
        deletedMessageIDs.insert(messageID)
        messages.removeAll { $0.id == messageID }
    }

    func reload(roomID: UUID) {
        guard isActive, self.roomID == roomID else { return }
        deletedMessageIDs.removeAll(keepingCapacity: true)
        startInitialLoad(roomID: roomID)
    }

    private func transition(to roomID: UUID?) {
        guard self.roomID != roomID || initialState == .idle else { return }
        cancelAndReset(roomID: roomID)
        guard let roomID else {
            initialState = .loaded
            olderState = .exhausted
            return
        }
        startInitialLoad(roomID: roomID)
    }

    private func startInitialLoad(roomID: UUID) {
        requestTask?.cancel()
        generation &+= 1
        let requestGeneration = generation
        messages.removeAll(keepingCapacity: false)
        nextCursor = nil
        initialState = .loading
        olderState = .idle
        let loader = loadPage
        let pageSize = pageSize
        requestTask = Task { [weak self] in
            do {
                let page = try await loader(roomID, nil, pageSize)
                guard !Task.isCancelled,
                      let self,
                      self.isCurrent(roomID: roomID, generation: requestGeneration)
                else { return }
                self.messages = MessageHistoryMerge.mergeConfirmed(
                    [],
                    with: page.messages.filter { !self.deletedMessageIDs.contains($0.id) },
                    roomID: roomID
                )
                self.nextCursor = page.nextCursor
                self.initialState = .loaded
                self.olderState = page.nextCursor == nil ? .exhausted : .idle
                self.requestTask = nil
            } catch is CancellationError {
                return
            } catch {
                guard let self,
                      self.isCurrent(roomID: roomID, generation: requestGeneration)
                else { return }
                self.initialState = .failed(
                    SideyBackendError.normalized(error).localizedDescription
                )
                self.olderState = .idle
                self.requestTask = nil
            }
        }
    }

    private func cancelAndReset(roomID: UUID?) {
        generation &+= 1
        requestTask?.cancel()
        requestTask = nil
        self.roomID = roomID
        messages.removeAll(keepingCapacity: false)
        deletedMessageIDs.removeAll(keepingCapacity: false)
        nextCursor = nil
        initialState = .idle
        olderState = .idle
    }

    private func isCurrent(roomID: UUID, generation: UInt64) -> Bool {
        isActive && self.roomID == roomID && self.generation == generation
    }
}

enum MessageHistoryMerge {
    static func mergeConfirmed(
        _ existing: [ChatMessage],
        with incoming: [ChatMessage],
        roomID: UUID
    ) -> [ChatMessage] {
        var byID: [UUID: ChatMessage] = [:]
        for message in existing where message.roomID == roomID {
            byID[message.id] = message
        }
        for message in incoming where message.roomID == roomID {
            byID[message.id] = message
        }
        return byID.values.sorted(by: newestFirst)
    }

    static func entries(
        pagedMessages: [ChatMessage],
        ledger: MessageLedger,
        outbox: MessageOutbox,
        roomID: UUID,
        now: Date = .now
    ) -> [MessageLedgerEntry] {
        let cutoff = now.addingTimeInterval(-MessageLedger.retentionInterval)
        var byID: [UUID: MessageLedgerEntry] = [:]

        for message in pagedMessages where message.roomID == roomID && message.createdAt >= cutoff {
            byID[message.id] = MessageLedgerEntry(
                id: message.id,
                roomID: message.roomID,
                senderID: message.senderID,
                body: message.body,
                createdAt: message.createdAt,
                state: .confirmed,
                bubbleStyleID: message.bubbleStyleID
            )
        }
        for entry in ledger.entries where entry.roomID == roomID && entry.createdAt >= cutoff {
            byID[entry.id] = entry
        }
        for message in outbox.entries where message.roomID == roomID && message.createdAt >= cutoff {
            guard byID[message.id] == nil else { continue }
            byID[message.id] = MessageLedgerEntry(
                id: message.id,
                roomID: message.roomID,
                senderID: message.senderID,
                body: message.body,
                createdAt: message.createdAt,
                state: message.state == .pending ? .pending : .failed,
                bubbleStyleID: message.bubbleStyleID
            )
        }
        return byID.values.sorted(by: displayOrder)
    }

    private static func newestFirst(_ lhs: ChatMessage, _ rhs: ChatMessage) -> Bool {
        lhs.createdAt == rhs.createdAt
            ? lhs.id.uuidString > rhs.id.uuidString
            : lhs.createdAt > rhs.createdAt
    }

    private static func newestFirst(_ lhs: MessageLedgerEntry, _ rhs: MessageLedgerEntry) -> Bool {
        lhs.createdAt == rhs.createdAt
            ? lhs.id.uuidString > rhs.id.uuidString
            : lhs.createdAt > rhs.createdAt
    }

    private static func displayOrder(_ lhs: MessageLedgerEntry, _ rhs: MessageLedgerEntry) -> Bool {
        let lhsIsLocal = lhs.state != .confirmed
        let rhsIsLocal = rhs.state != .confirmed
        if lhsIsLocal != rhsIsLocal {
            // The device clock can lag behind the server. Keep unresolved local
            // sends after confirmed history so their pending/failed state stays visible.
            return !lhsIsLocal
        }
        return newestFirst(rhs, lhs)
    }
}

struct MessageHistoryParticipant: Equatable {
    let nickname: String
    let characterID: String
    let isCurrentUser: Bool
}

enum MessageHistoryParticipantResolver {
    static func resolve(
        senderID: UUID,
        in room: Room?,
        currentUserID: UUID?
    ) -> MessageHistoryParticipant {
        guard let member = room?.members.first(where: { $0.userID == senderID }) else {
            return MessageHistoryParticipant(
                nickname: L10n.text("profile.nickname.unknown_friend"),
                characterID: PixelCharacterCatalog.pixelHamsterID,
                isCurrentUser: senderID == currentUserID
            )
        }
        return MessageHistoryParticipant(
            nickname: ProfileValidator.displayNickname(member.nickname),
            characterID: PixelCharacterCatalog.canonicalID(for: member.characterID),
            isCurrentUser: senderID == currentUserID
        )
    }
}
