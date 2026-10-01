import XCTest
@testable import SIDEY

final class RealtimeRolloutPolicyTests: XCTestCase {
    private let contractHash = String(repeating: "a", count: 64)
    private let identity = RealtimeCredentialIdentity(
        accountID: UUID(uuidString: "11111111-1111-4111-8111-111111111111")!,
        sessionID: UUID(uuidString: "22222222-2222-4222-8222-222222222222")!
    )

    func testEnabledExactContractSelectsFirebaseAndPersistsFailClosedState() async throws {
        let cache = RealtimeRolloutCacheStub()
        let fetchedResponse = response(enabled: true, transport: "firebase_v2")

        let decision = try await RealtimeRolloutPolicyResolver.resolve(
            identity: identity,
            contractHash: contractHash,
            cache: cache,
            fetch: { fetchedResponse },
            now: { Date(timeIntervalSince1970: 1_000) }
        )

        XCTAssertEqual(decision.state, .firebaseV2)
        XCTAssertEqual(cache.record?.state, .firebaseV2)
        XCTAssertEqual(cache.record?.accountID, identity.accountID)
        XCTAssertEqual(cache.record?.sessionID, identity.sessionID)
        XCTAssertEqual(
            cache.record?.expiresAt,
            Date(timeIntervalSince1970: 1_300)
        )
    }

    func testThirtyMinuteSelectorPersistsServerExpiry() async throws {
        let cache = RealtimeRolloutCacheStub()
        let fetchedResponse = response(enabled: true, transport: "firebase_v2", cacheTTLSeconds: 1_800)
        let decision = try await RealtimeRolloutPolicyResolver.resolve(
            identity: identity,
            contractHash: contractHash,
            cache: cache,
            fetch: { fetchedResponse },
            now: { Date(timeIntervalSince1970: 1_000) }
        )
        XCTAssertEqual(decision.state, .firebaseV2)
        XCTAssertEqual(decision.cacheTTLSeconds, 1_800)
        XCTAssertEqual(cache.record?.expiresAt, Date(timeIntervalSince1970: 2_800))
    }

    func testSelectorRejectsCacheLifetimeOutsideThirtyMinuteBound() {
        for lifetime in [0, 1_801] {
            XCTAssertThrowsError(try RealtimeRolloutPolicyEvaluator.evaluate(
                response(enabled: true, transport: "firebase_v2", cacheTTLSeconds: lifetime),
                expectedContractHash: contractHash
            )) { error in
                XCTAssertEqual(error as? RealtimeRolloutPolicyError, .malformedResponse)
            }
        }
    }

    func testKillSwitchExplicitlySelectsLegacyAndReplacesEnabledCache() async throws {
        let cache = RealtimeRolloutCacheStub(record: record(state: .firebaseV2))
        let fetchedResponse = response(
            enabled: false,
            transport: "legacy_supabase",
            killSwitch: true,
            contractHash: String(repeating: "b", count: 64)
        )

        let decision = try await RealtimeRolloutPolicyResolver.resolve(
            identity: identity,
            contractHash: contractHash,
            cache: cache,
            fetch: { fetchedResponse }
        )

        XCTAssertEqual(decision.state, .legacy)
        XCTAssertEqual(cache.record?.state, .legacy)
    }

    func testPolicyOutageNeverDowngradesPreviouslyEnabledSession() async throws {
        let cache = RealtimeRolloutCacheStub(record: record(state: .firebaseV2))

        await XCTAssertThrowsErrorAsync(
            RealtimeRolloutPolicyError.enabledPolicyUnavailable
        ) {
            _ = try await RealtimeRolloutPolicyResolver.resolve(
                identity: self.identity,
                contractHash: self.contractHash,
                cache: cache,
                fetch: { throw RolloutStubError.unavailable }
            )
        }
    }

    func testPolicyOutageAllowsLegacyWithoutPriorEnabledDecision() async throws {
        let cache = RealtimeRolloutCacheStub()

        let decision = try await RealtimeRolloutPolicyResolver.resolve(
            identity: identity,
            contractHash: contractHash,
            cache: cache,
            fetch: { throw RolloutStubError.unavailable }
        )

        XCTAssertEqual(decision.state, .legacy)
    }

    func testPolicyOutageFailsClosedWhenCachedDecisionCannotBeRead() async {
        let cache = RealtimeRolloutCacheStub(loadError: .unavailable)

        await XCTAssertThrowsErrorAsync(RealtimeRolloutPolicyError.cacheUnavailable) {
            _ = try await RealtimeRolloutPolicyResolver.resolve(
                identity: self.identity,
                contractHash: self.contractHash,
                cache: cache,
                fetch: { throw RolloutStubError.unavailable }
            )
        }
    }

    func testEnabledDecisionFailsWhenFailClosedCacheCannotBeSaved() async throws {
        let cache = RealtimeRolloutCacheStub(saveError: .unavailable)
        let fetchedResponse = response(enabled: true, transport: "firebase_v2")

        await XCTAssertThrowsErrorAsync(RealtimeRolloutPolicyError.cacheUnavailable) {
            _ = try await RealtimeRolloutPolicyResolver.resolve(
                identity: self.identity,
                contractHash: self.contractHash,
                cache: cache,
                fetch: { fetchedResponse }
            )
        }
    }

    func testEnabledContractMismatchFailsInsteadOfUsingLegacy() {
        XCTAssertThrowsError(try RealtimeRolloutPolicyEvaluator.evaluate(
            response(
                enabled: true,
                transport: "firebase_v2",
                contractHash: String(repeating: "b", count: 64)
            ),
            expectedContractHash: contractHash
        )) { error in
            XCTAssertEqual(
                error as? RealtimeRolloutPolicyError,
                .enabledContractMismatch
            )
        }
    }

    func testEnabledLegacyTransportIsRejectedAsInconsistent() {
        XCTAssertThrowsError(try RealtimeRolloutPolicyEvaluator.evaluate(
            response(enabled: true, transport: "legacy_supabase"),
            expectedContractHash: contractHash
        ))
    }

    func testResponseDecoderRejectsUnknownContractKeys() throws {
        let data = try XCTUnwrap("""
        {
          "enabled": true,
          "protocolVersion": 2,
          "transport": "firebase_v2",
          "contractHash": "\(contractHash)",
          "killSwitch": false,
          "cacheTtlSeconds": 300,
          "failureMode": "fail_closed_if_last_enabled",
          "unexpected": true
        }
        """.data(using: .utf8))

        XCTAssertThrowsError(
            try JSONDecoder().decode(RealtimeRolloutPolicyResponse.self, from: data)
        )
    }

    func testProductionAppVersionIncludesMarketingAndBuildVersions() throws {
        XCTAssertEqual(
            try FirebaseV2ProductionFactory.appVersion(bundleInfo: [
                "CFBundleShortVersionString": "1.3.0",
                "CFBundleVersion": "33",
            ]),
            "1.3.0+33"
        )
    }

    func testProductionAppVersionRequiresBuildVersion() {
        XCTAssertThrowsError(try FirebaseV2ProductionFactory.appVersion(bundleInfo: [
            "CFBundleShortVersionString": "1.3.0",
        ])) { error in
            XCTAssertEqual(
                error as? FirebaseV2ProductionFactoryError,
                .appVersionUnavailable
            )
        }
    }

    func testLeaseRenewalRegistersSelectorBeforeBootstrap() async throws {
        let recorder = RolloutRenewalRecorder()

        let result = try await RealtimeRolloutRenewal.perform(
            leaseDue: true,
            refreshPolicy: {
                await recorder.record("selector")
                return RealtimeRolloutPolicyDecision(
                    state: .firebaseV2,
                    cacheTTLSeconds: 300
                )
            },
            refreshLease: {
                await recorder.record("bootstrap")
            }
        )

        let operations = await recorder.operations
        XCTAssertEqual(operations, ["selector", "bootstrap"])
        XCTAssertTrue(result.leaseRenewed)
    }

    func testLegacySelectorDecisionNeverBootstrapsAnotherLease() async throws {
        let recorder = RolloutRenewalRecorder()

        let result = try await RealtimeRolloutRenewal.perform(
            leaseDue: true,
            refreshPolicy: {
                await recorder.record("selector")
                return RealtimeRolloutPolicyDecision(
                    state: .legacy,
                    cacheTTLSeconds: 30
                )
            },
            refreshLease: {
                await recorder.record("bootstrap")
            }
        )

        let operations = await recorder.operations
        XCTAssertEqual(operations, ["selector"])
        XCTAssertEqual(result.decision.state, .legacy)
        XCTAssertFalse(result.leaseRenewed)
    }

    func testSelectorFailureNeverBootstrapsAndPropagatesForRetry() async {
        let recorder = RolloutRenewalRecorder()

        await XCTAssertThrowsErrorAsync(RolloutStubError.unavailable) {
            _ = try await RealtimeRolloutRenewal.perform(
                leaseDue: true,
                refreshPolicy: {
                    await recorder.record("selector")
                    throw RolloutStubError.unavailable
                },
                refreshLease: {
                    await recorder.record("bootstrap")
                }
            )
        }

        let operations = await recorder.operations
        XCTAssertEqual(operations, ["selector"])
    }

    func testObservedLeaseDisappearanceTriggersImmediateRecovery() {
        XCTAssertEqual(
            RealtimeRolloutTiming.leaseDelay(
                nil,
                observedFirebaseLease: true,
                fallback: .seconds(300)
            ),
            .zero
        )
        XCTAssertTrue(RealtimeRolloutTiming.isLeaseDue(
            nil,
            observedFirebaseLease: true
        ))
    }

    func testLegacyTransportWithoutObservedLeaseUsesPolicySchedule() {
        XCTAssertEqual(
            RealtimeRolloutTiming.leaseDelay(
                nil,
                observedFirebaseLease: false,
                fallback: .seconds(42)
            ),
            .seconds(42)
        )
        XCTAssertFalse(RealtimeRolloutTiming.isLeaseDue(
            nil,
            observedFirebaseLease: false
        ))
    }

    func testFreshLeaseStatusControlsRenewalDecision() {
        XCTAssertFalse(RealtimeRolloutTiming.isLeaseDue(
            RealtimeRolloutLeaseStatus(
                refreshIn: .seconds(30),
                expiresIn: .seconds(60)
            ),
            observedFirebaseLease: true
        ))
        XCTAssertTrue(RealtimeRolloutTiming.isLeaseDue(
            RealtimeRolloutLeaseStatus(
                refreshIn: .zero,
                expiresIn: .seconds(30)
            ),
            observedFirebaseLease: true
        ))
    }

    func testOperationCancellationDoesNotStopHealthyMonitorTask() {
        XCTAssertFalse(RealtimeRolloutTiming.shouldStopAfterError(
            CancellationError(),
            taskIsCancelled: false
        ))
        XCTAssertTrue(RealtimeRolloutTiming.shouldStopAfterError(
            CancellationError(),
            taskIsCancelled: true
        ))
    }

    private func response(
        enabled: Bool,
        transport: String,
        killSwitch: Bool = false,
        contractHash: String? = nil,
        cacheTTLSeconds: Int = 300
    ) -> RealtimeRolloutPolicyResponse {
        RealtimeRolloutPolicyResponse(
            enabled: enabled,
            protocolVersion: 2,
            transport: transport,
            contractHash: contractHash ?? self.contractHash,
            killSwitch: killSwitch,
            cacheTTLSeconds: cacheTTLSeconds,
            failureMode: "fail_closed_if_last_enabled"
        )
    }

    private func record(state: RealtimeRolloutPolicyState) -> RealtimeRolloutCacheRecord {
        RealtimeRolloutCacheRecord(
            accountID: identity.accountID,
            sessionID: identity.sessionID,
            contractHash: contractHash,
            state: state,
            recordedAt: Date(timeIntervalSince1970: 500),
            expiresAt: Date(timeIntervalSince1970: 800)
        )
    }
}

private enum RolloutStubError: Error {
    case unavailable
}

private actor RolloutRenewalRecorder {
    private(set) var operations: [String] = []

    func record(_ operation: String) {
        operations.append(operation)
    }
}

private final class RealtimeRolloutCacheStub: @unchecked Sendable,
    RealtimeRolloutPolicyCaching {
    private let lock = NSLock()
    private var storedRecord: RealtimeRolloutCacheRecord?
    private let loadError: RolloutStubError?
    private let saveError: RolloutStubError?

    init(
        record: RealtimeRolloutCacheRecord? = nil,
        loadError: RolloutStubError? = nil,
        saveError: RolloutStubError? = nil
    ) {
        self.storedRecord = record
        self.loadError = loadError
        self.saveError = saveError
    }

    var record: RealtimeRolloutCacheRecord? {
        lock.withLock { storedRecord }
    }

    func load(
        identity: RealtimeCredentialIdentity,
        contractHash: String
    ) throws -> RealtimeRolloutCacheRecord? {
        if let loadError { throw loadError }
        return lock.withLock { storedRecord }
    }

    func save(_ record: RealtimeRolloutCacheRecord) throws {
        if let saveError { throw saveError }
        lock.withLock { storedRecord = record }
    }
}

private func XCTAssertThrowsErrorAsync<T: Error & Equatable>(
    _ expected: T,
    operation: () async throws -> Void,
    file: StaticString = #filePath,
    line: UInt = #line
) async {
    do {
        try await operation()
        XCTFail("Expected error \(expected)", file: file, line: line)
    } catch {
        XCTAssertEqual(error as? T, expected, file: file, line: line)
    }
}
