import Foundation

enum FirebaseV2BootstrapRecoveryPolicy {
    static func isTransient(_ error: any Error) -> Bool {
        if let urlError = error as? URLError {
            return urlError.code != .cancelled
        }
        guard let bootstrapError = error as? FirebaseV2BootstrapClientError,
              case .server(let failure) = bootstrapError else {
            return false
        }
        switch failure {
        case .rateLimited, .unavailable, .grantNotConverged:
            return true
        case .unexpectedStatus(let status):
            return (500...599).contains(status)
        case .invalidArgument, .authenticationRequired, .methodNotAllowed,
             .rolloutDisabled:
            return false
        }
    }
}

enum FirebaseV2BootstrapRetryPolicy {
    static let maximumLocalDelaySeconds = 300.0

    static func delaySeconds(
        failureCount: Int,
        rateLimited: Bool,
        retryAfterSeconds: TimeInterval?,
        jitter: Double
    ) -> TimeInterval {
        let exponent = min(max(failureCount - 1, 0), 16)
        let base = min(5.0 * pow(2.0, Double(exponent)), maximumLocalDelaySeconds)
        let clampedJitter = min(max(jitter, 0), 1)
        let local = min(base * (0.8 + 0.4 * clampedJitter), maximumLocalDelaySeconds)
        let minimum = rateLimited ? 30.0 : 0.0
        let server = retryAfterSeconds.flatMap { $0.isFinite && $0 >= 0 ? min($0, 604_800) : nil } ?? 0
        let required = max(minimum, server)
        return max(local, required) + (required > 0 ? required * 0.2 * clampedJitter : 0)
    }

    static func retryAfterSeconds(
        from response: HTTPURLResponse,
        now: Date = Date()
    ) -> TimeInterval? {
        guard let raw = response.value(forHTTPHeaderField: "Retry-After")?
            .trimmingCharacters(in: .whitespacesAndNewlines),
              !raw.isEmpty else { return nil }
        if let seconds = Int64(raw), seconds >= 0 {
            return min(TimeInterval(seconds), 604_800)
        }
        let serverNow = response.value(forHTTPHeaderField: "Date")
            .flatMap(httpDate) ?? now
        guard let retryDate = httpDate(raw) else { return nil }
        return min(max(0, retryDate.timeIntervalSince(serverNow)), 604_800)
    }

    private static func httpDate(_ raw: String) -> Date? {
        for format in [
            "EEE',' dd MMM yyyy HH':'mm':'ss z",
            "EEEE',' dd'-'MMM'-'yy HH':'mm':'ss z",
            "EEE MMM d HH':'mm':'ss yyyy",
        ] {
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.timeZone = TimeZone(secondsFromGMT: 0)
            formatter.dateFormat = format
            if let date = formatter.date(from: raw) {
                return date
            }
        }
        return nil
    }
}

/// Shares the HTTP retry deadline across router replacements for one Supabase session.
/// It retains only session UUIDs and monotonic deadlines, never access tokens.
actor FirebaseV2BootstrapRetryGate {
    static let shared = FirebaseV2BootstrapRetryGate()

    private struct Key: Hashable {
        let accountID: UUID
        let sessionID: UUID
    }

    private struct State {
        var failures = 0
        var notBefore: ContinuousClock.Instant?
        var lastSuccess: ContinuousClock.Instant?
    }

    private let clock = ContinuousClock()
    private var states: [Key: State] = [:]

    func wait(for identity: RealtimeCredentialIdentity) async throws {
        let key = Key(accountID: identity.accountID, sessionID: identity.sessionID)
        while let deadline = states[key]?.notBefore, clock.now < deadline {
            try await clock.sleep(until: deadline)
        }
        try Task.checkCancellation()
    }

    func failed(
        for identity: RealtimeCredentialIdentity,
        rateLimited: Bool,
        retryAfterSeconds: TimeInterval?,
        jitter: Double = Double.random(in: 0...1)
    ) {
        let key = Key(accountID: identity.accountID, sessionID: identity.sessionID)
        var state = states[key] ?? State()
        if let lastSuccess = state.lastSuccess,
           lastSuccess.duration(to: clock.now) >= .seconds(30) {
            state.failures = 0
        }
        state.failures = min(state.failures + 1, 17)
        state.lastSuccess = nil
        let delay = FirebaseV2BootstrapRetryPolicy.delaySeconds(
            failureCount: state.failures,
            rateLimited: rateLimited,
            retryAfterSeconds: retryAfterSeconds,
            jitter: jitter
        )
        let deadline = clock.now.advanced(by: .seconds(delay))
        if let current = state.notBefore, current > deadline {
            state.notBefore = current
        } else {
            state.notBefore = deadline
        }
        states[key] = state
    }

    func succeeded(for identity: RealtimeCredentialIdentity) {
        let key = Key(accountID: identity.accountID, sessionID: identity.sessionID)
        var state = states[key] ?? State()
        state.lastSuccess = clock.now
        state.notBefore = nil
        states[key] = state
    }
}
