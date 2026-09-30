import Foundation

/// Remembers semantic state per room subscription. Timestamps are metadata,
/// not a reason to publish an otherwise identical Presence payload.
struct PresencePublicationLedger {
    private struct Publication: Equatable {
        let channelGeneration: Int
        let state: PresenceState
    }

    private var published: [UUID: Publication] = [:]
    private(set) var invalidationRevision: UInt64 = 0

    func needsPublication(roomID: UUID, channelGeneration: Int, state: PresenceState) -> Bool {
        published[roomID] != Publication(channelGeneration: channelGeneration, state: state)
    }

    @discardableResult
    mutating func didPublish(
        roomID: UUID, channelGeneration: Int, state: PresenceState,
        observedRevision: UInt64
    ) -> Bool {
        guard observedRevision == invalidationRevision else { return false }
        published[roomID] = Publication(channelGeneration: channelGeneration, state: state)
        return true
    }

    mutating func invalidate(roomID: UUID) {
        invalidationRevision &+= 1
        published.removeValue(forKey: roomID)
    }

    mutating func invalidateAll() {
        invalidationRevision &+= 1
        published.removeAll()
    }
}

/// Serializes complete Presence publications while retaining only the newest
/// state that has not started yet. Callers whose intermediate state is
/// coalesced complete with the newer full publication.
actor PresencePublicationQueue<State: Sendable> {
    typealias Publish = @Sendable (State) async throws -> Void

    private struct Pending: Sendable {
        let revision: UInt64
        let state: State
    }

    private let publish: Publish
    private var nextRevision: UInt64 = 0
    private var latest: Pending?
    private var waiters: [UInt64: CheckedContinuation<Void, any Error>] = [:]
    private var worker: Task<Void, Never>?

    init(publish: @escaping Publish) {
        self.publish = publish
    }

    func submit(_ state: State) async throws {
        nextRevision &+= 1
        let revision = nextRevision
        latest = Pending(revision: revision, state: state)

        try await withCheckedThrowingContinuation { continuation in
            waiters[revision] = continuation
            startWorkerIfNeeded()
        }
    }

    func cancel() {
        worker?.cancel()
        worker = nil
        latest = nil
        let pendingWaiters = waiters.values
        waiters.removeAll()
        for waiter in pendingWaiters {
            waiter.resume(throwing: CancellationError())
        }
    }

    private func startWorkerIfNeeded() {
        guard worker == nil else { return }
        worker = Task { [weak self] in
            await self?.drain()
        }
    }

    private func drain() async {
        while !Task.isCancelled, let pending = latest {
            latest = nil
            do {
                try await publish(pending.state)
                resumeWaiters(through: pending.revision, result: .success(()))
            } catch {
                // A newer full state can still repair a failed partial batch.
                // Keep older callers waiting for that state instead of making
                // a transient superseded failure observable.
                if latest != nil { continue }
                resumeWaiters(through: pending.revision, result: .failure(error))
            }
        }
        worker = nil
        if latest != nil { startWorkerIfNeeded() }
    }

    private func resumeWaiters(
        through revision: UInt64,
        result: Result<Void, any Error>
    ) {
        let completed = waiters.keys.filter { $0 <= revision }
        for key in completed {
            guard let waiter = waiters.removeValue(forKey: key) else { continue }
            waiter.resume(with: result)
        }
    }
}
