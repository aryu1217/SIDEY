import Foundation

struct RealtimeRolloutPolicyResponse: Decodable, Equatable, Sendable {
    let enabled: Bool
    let protocolVersion: Int
    let transport: String
    let contractHash: String
    let killSwitch: Bool
    let cacheTTLSeconds: Int
    let failureMode: String

    private enum CodingKeys: String, CodingKey {
        case enabled
        case protocolVersion
        case transport
        case contractHash
        case killSwitch
        case cacheTTLSeconds = "cacheTtlSeconds"
        case failureMode
    }

    private struct DynamicCodingKey: CodingKey {
        let stringValue: String
        let intValue: Int? = nil

        init?(stringValue: String) {
            self.stringValue = stringValue
        }

        init?(intValue: Int) {
            return nil
        }
    }

    init(
        enabled: Bool,
        protocolVersion: Int,
        transport: String,
        contractHash: String,
        killSwitch: Bool,
        cacheTTLSeconds: Int,
        failureMode: String
    ) {
        self.enabled = enabled
        self.protocolVersion = protocolVersion
        self.transport = transport
        self.contractHash = contractHash
        self.killSwitch = killSwitch
        self.cacheTTLSeconds = cacheTTLSeconds
        self.failureMode = failureMode
    }

    init(from decoder: any Decoder) throws {
        let dynamic = try decoder.container(keyedBy: DynamicCodingKey.self)
        let expectedKeys: Set<String> = [
            "enabled",
            "protocolVersion",
            "transport",
            "contractHash",
            "killSwitch",
            "cacheTtlSeconds",
            "failureMode",
        ]
        guard Set(dynamic.allKeys.map(\.stringValue)) == expectedKeys else {
            throw DecodingError.dataCorrupted(.init(
                codingPath: decoder.codingPath,
                debugDescription: "Realtime rollout response keys do not match the v2 contract."
            ))
        }

        let values = try decoder.container(keyedBy: CodingKeys.self)
        enabled = try values.decode(Bool.self, forKey: .enabled)
        protocolVersion = try values.decode(Int.self, forKey: .protocolVersion)
        transport = try values.decode(String.self, forKey: .transport)
        contractHash = try values.decode(String.self, forKey: .contractHash)
        killSwitch = try values.decode(Bool.self, forKey: .killSwitch)
        cacheTTLSeconds = try values.decode(Int.self, forKey: .cacheTTLSeconds)
        failureMode = try values.decode(String.self, forKey: .failureMode)
    }
}

enum RealtimeRolloutPolicyState: String, Codable, Equatable, Sendable {
    case legacy
    case firebaseV2
}

struct RealtimeRolloutPolicyDecision: Equatable, Sendable {
    let state: RealtimeRolloutPolicyState
    let cacheTTLSeconds: Int
}

struct RealtimeRolloutRenewalResult: Equatable, Sendable {
    let decision: RealtimeRolloutPolicyDecision
    let leaseRenewed: Bool
}

enum RealtimeRolloutTiming {
    static func earlyRefreshDelayMilliseconds(
        serverDelay: Int64,
        expiresIn: Int64,
        lead: Int64
    ) -> Int64 {
        guard expiresIn > 0 else { return 0 }
        let margin = min(max(lead, 0), max(1, expiresIn / 2))
        return min(max(0, serverDelay), max(0, expiresIn - margin))
    }

    static func leaseDelay(
        _ lease: RealtimeRolloutLeaseStatus?,
        observedFirebaseLease: Bool,
        fallback: Duration
    ) -> Duration {
        if let lease { return lease.refreshIn }
        return observedFirebaseLease ? .zero : fallback
    }

    static func isLeaseDue(
        _ lease: RealtimeRolloutLeaseStatus?,
        observedFirebaseLease: Bool
    ) -> Bool {
        guard let lease else { return observedFirebaseLease }
        return lease.refreshIn <= .milliseconds(1)
    }

    static func shouldStopAfterError(
        _ error: any Error,
        taskIsCancelled: Bool
    ) -> Bool {
        // SDK/URLSession operations may independently throw CancellationError
        // while the owning monitor task is still healthy.
        if error is CancellationError { return taskIsCancelled }
        return taskIsCancelled
    }
}

enum RealtimeRolloutRenewal {
    /// Re-registers authenticated capability before any bootstrap is allowed.
    /// A legacy/kill-switch decision never mints another Firebase credential.
    static func perform(
        leaseDue: Bool,
        refreshPolicy: @Sendable () async throws -> RealtimeRolloutPolicyDecision,
        refreshLease: @Sendable () async throws -> Void
    ) async throws -> RealtimeRolloutRenewalResult {
        let decision = try await refreshPolicy()
        guard decision.state == .firebaseV2, leaseDue else {
            return RealtimeRolloutRenewalResult(
                decision: decision,
                leaseRenewed: false
            )
        }
        try await refreshLease()
        return RealtimeRolloutRenewalResult(
            decision: decision,
            leaseRenewed: true
        )
    }
}

enum RealtimeRolloutPolicyError: LocalizedError, Equatable {
    case malformedResponse
    case enabledContractMismatch
    case enabledPolicyUnavailable
    case cacheUnavailable

    var errorDescription: String? {
        switch self {
        case .malformedResponse:
            L10n.text("realtime.rollout.error.malformed_response")
        case .enabledContractMismatch:
            L10n.text("realtime.rollout.error.contract_mismatch")
        case .enabledPolicyUnavailable:
            L10n.text("realtime.rollout.error.enabled_policy_unavailable")
        case .cacheUnavailable:
            L10n.text("realtime.rollout.error.cache_unavailable")
        }
    }
}

enum RealtimeRolloutPolicyEvaluator {
    static let protocolVersion = 2
    static let platform = "macos"
    static let failureMode = "fail_closed_if_last_enabled"
    static let firebaseTransport = "firebase_v2"
    static let legacyTransport = "legacy_supabase"

    static func evaluate(
        _ response: RealtimeRolloutPolicyResponse,
        expectedContractHash: String
    ) throws -> RealtimeRolloutPolicyDecision {
        guard response.protocolVersion == protocolVersion,
              response.failureMode == failureMode,
              (1...3_600).contains(response.cacheTTLSeconds),
              isSHA256(response.contractHash),
              [firebaseTransport, legacyTransport].contains(response.transport)
        else {
            throw RealtimeRolloutPolicyError.malformedResponse
        }

        if response.killSwitch || !response.enabled {
            guard response.transport == legacyTransport else {
                throw RealtimeRolloutPolicyError.malformedResponse
            }
            return RealtimeRolloutPolicyDecision(
                state: .legacy,
                cacheTTLSeconds: response.cacheTTLSeconds
            )
        }

        guard response.transport == firebaseTransport,
              response.contractHash == expectedContractHash else {
            throw RealtimeRolloutPolicyError.enabledContractMismatch
        }
        return RealtimeRolloutPolicyDecision(
            state: .firebaseV2,
            cacheTTLSeconds: response.cacheTTLSeconds
        )
    }

    private static func isSHA256(_ value: String) -> Bool {
        value.count == 64 && value.allSatisfy { character in
            character.isNumber || ("a"..."f").contains(String(character))
        }
    }
}

struct RealtimeRolloutCacheRecord: Codable, Equatable, Sendable {
    let accountID: UUID
    let sessionID: UUID
    let contractHash: String
    let state: RealtimeRolloutPolicyState
    let recordedAt: Date
    let expiresAt: Date
}

protocol RealtimeRolloutPolicyCaching: Sendable {
    func load(
        identity: RealtimeCredentialIdentity,
        contractHash: String
    ) throws -> RealtimeRolloutCacheRecord?
    func save(_ record: RealtimeRolloutCacheRecord) throws
}

struct KeychainRealtimeRolloutPolicyCache: RealtimeRolloutPolicyCaching {
    private let keychain: KeychainStore
    private let backendFingerprint: String

    init(keychain: KeychainStore, backendFingerprint: String) {
        self.keychain = keychain
        self.backendFingerprint = backendFingerprint
    }

    func load(
        identity: RealtimeCredentialIdentity,
        contractHash: String
    ) throws -> RealtimeRolloutCacheRecord? {
        guard let data = try keychain.read(account: account(identity)) else { return nil }
        let record = try JSONDecoder().decode(RealtimeRolloutCacheRecord.self, from: data)
        guard record.accountID == identity.accountID,
              record.sessionID == identity.sessionID,
              record.contractHash == contractHash else { return nil }
        return record
    }

    func save(_ record: RealtimeRolloutCacheRecord) throws {
        try keychain.write(
            try JSONEncoder().encode(record),
            account: account(RealtimeCredentialIdentity(
                accountID: record.accountID,
                sessionID: record.sessionID
            ))
        )
    }

    private func account(_ identity: RealtimeCredentialIdentity) -> String {
        "realtime-rollout:\(backendFingerprint):\(identity.accountID.uuidString.lowercased()):\(identity.sessionID.uuidString.lowercased())"
    }
}

enum RealtimeRolloutPolicyResolver {
    static func resolve(
        identity: RealtimeCredentialIdentity,
        contractHash: String,
        cache: any RealtimeRolloutPolicyCaching,
        fetch: @Sendable () async throws -> RealtimeRolloutPolicyResponse,
        now: @Sendable () -> Date = Date.init
    ) async throws -> RealtimeRolloutPolicyDecision {
        do {
            let decision = try RealtimeRolloutPolicyEvaluator.evaluate(
                try await fetch(),
                expectedContractHash: contractHash
            )
            let recordedAt = now()
            let record = RealtimeRolloutCacheRecord(
                accountID: identity.accountID,
                sessionID: identity.sessionID,
                contractHash: contractHash,
                state: decision.state,
                recordedAt: recordedAt,
                expiresAt: recordedAt.addingTimeInterval(TimeInterval(decision.cacheTTLSeconds))
            )
            do {
                try cache.save(record)
            } catch {
                // An enabled decision must survive a relaunch so a temporary
                // policy outage can never silently downgrade this session.
                guard decision.state == .legacy else {
                    throw RealtimeRolloutPolicyError.cacheUnavailable
                }
            }
            return decision
        } catch let policyError as RealtimeRolloutPolicyError {
            switch policyError {
            case .malformedResponse, .enabledContractMismatch:
                throw policyError
            case .enabledPolicyUnavailable, .cacheUnavailable:
                throw policyError
            }
        } catch {
            let cached: RealtimeRolloutCacheRecord?
            do {
                cached = try cache.load(identity: identity, contractHash: contractHash)
            } catch {
                throw RealtimeRolloutPolicyError.cacheUnavailable
            }
            guard cached?.state != .firebaseV2 else {
                throw RealtimeRolloutPolicyError.enabledPolicyUnavailable
            }
            return RealtimeRolloutPolicyDecision(state: .legacy, cacheTTLSeconds: 0)
        }
    }
}
