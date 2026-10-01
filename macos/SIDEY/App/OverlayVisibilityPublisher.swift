import Foundation

/// One pending value, one request and one timer regardless of how often UI is toggled.
@MainActor
final class OverlayVisibilityPublisher {
    static let debounce: TimeInterval = 0.5
    static let minimumInterval: TimeInterval = 3.1
    private let now: () -> TimeInterval
    private let automaticallySchedule: Bool
    private let publish: @MainActor (Bool) async throws -> Void
    private var desired: Bool?
    private var confirmed: Bool?
    private var changedAt: TimeInterval = 0
    private var lastAttempt: TimeInterval?
    private var retryAt: TimeInterval?
    private var generation = 0
    private var timer: Task<Void, Never>?
    private var request: Task<Void, Never>?
    private var stopped = false
    private var repairAttemptsRemaining = 1

    init(now: @escaping () -> TimeInterval,
         automaticallySchedule: Bool = true, publish: @escaping @MainActor (Bool) async throws -> Void) {
        self.now = now
        self.automaticallySchedule = automaticallySchedule
        self.publish = publish
    }

    func update(suppressed: Bool) {
        guard !stopped else { return }
        if desired != suppressed {
            changedAt = now(); desired = suppressed
            repairAttemptsRemaining = 1
        }
        processDeadlines()
    }

    /// Authentication and connection recovery reassert the last UI state once.
    func reconnect(suppressed: Bool) {
        guard !stopped else { return }
        confirmed = nil
        desired = suppressed
        changedAt = now()
        retryAt = nil
        repairAttemptsRemaining = 1
        processDeadlines()
    }

    func processDeadlines() {
        timer?.cancel(); timer = nil
        guard !stopped, request == nil, let desired, desired != confirmed else { return }
        let due = max(changedAt + Self.debounce, (lastAttempt ?? -.infinity) + Self.minimumInterval,
                      retryAt ?? -.infinity)
        guard now() >= due else {
            guard automaticallySchedule else { return }
            timer = Task { [weak self] in
                do { try await Task.sleep(for: .seconds(max(0, due - (self?.now() ?? due)))) }
                catch { return }
                self?.processDeadlines()
            }
            return
        }
        lastAttempt = now()
        retryAt = nil
        let currentGeneration = generation
        request = Task { [weak self] in
            guard let self else { return }
            var succeeded = false
            var rateRejected = false
            do { try await publish(desired); succeeded = true }
            catch {
                rateRejected = error is OverlayVisibilityRateLimitError
            }
            guard generation == currentGeneration, !stopped else { return }
            request = nil
            if succeeded { confirmed = desired }
            else if rateRejected {
                retryAt = now() + Self.minimumInterval
            } else {
                // The server may have committed before the response disappeared. The
                // old confirmation cannot suppress a newer opposite value. Repair once,
                // then wait for another user change or connection recovery.
                confirmed = nil
                if repairAttemptsRemaining > 0 {
                    repairAttemptsRemaining -= 1
                    retryAt = now() + 15
                }
            }
            if succeeded || retryAt != nil || self.desired != desired { processDeadlines() }
        }
    }

    func reset() {
        generation &+= 1
        timer?.cancel(); timer = nil
        request?.cancel(); request = nil
        desired = nil; confirmed = nil; retryAt = nil; lastAttempt = nil
        repairAttemptsRemaining = 1
    }

    func stop() { reset(); stopped = true }
    func drain() async { await request?.value }
}

struct OverlayVisibilityRateLimitError: Error {}
