import XCTest
@testable import SIDEY

final class FirebaseV2BootstrapRetryTests: XCTestCase {
    func testRetryAfterSecondsAndHTTPDateTakePriorityOverLocalBackoff() throws {
        let url = try XCTUnwrap(URL(string: "https://example.test/bootstrap"))
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let seconds = try XCTUnwrap(HTTPURLResponse(
            url: url,
            statusCode: 429,
            httpVersion: nil,
            headerFields: ["Retry-After": "60"]
        ))
        XCTAssertEqual(FirebaseV2BootstrapRetryPolicy.retryAfterSeconds(from: seconds, now: now), 60)

        let date = try XCTUnwrap(HTTPURLResponse(
            url: url,
            statusCode: 503,
            httpVersion: nil,
            headerFields: ["Retry-After": "Thu, 01 Jan 1970 00:01:00 GMT"]
        ))
        XCTAssertEqual(
            FirebaseV2BootstrapRetryPolicy.retryAfterSeconds(
                from: date, now: Date(timeIntervalSince1970: 0)
            ),
            60
        )

        let skewedClient = try XCTUnwrap(HTTPURLResponse(
            url: url,
            statusCode: 429,
            httpVersion: nil,
            headerFields: [
                "Date": "Thu, 01 Jan 1970 00:00:00 GMT",
                "Retry-After": "Thu, 01 Jan 1970 00:01:00 GMT",
            ]
        ))
        XCTAssertEqual(FirebaseV2BootstrapRetryPolicy.retryAfterSeconds(
            from: skewedClient,
            now: Date(timeIntervalSince1970: 1_800_000_000)
        ), 60)
    }

    func testRetryPolicyHonorsServerFloorAndBoundsLocalBackoff() {
        XCTAssertEqual(FirebaseV2BootstrapRetryPolicy.delaySeconds(
            failureCount: 1, rateLimited: true, retryAfterSeconds: 60, jitter: 0
        ), 60)
        XCTAssertEqual(FirebaseV2BootstrapRetryPolicy.delaySeconds(
            failureCount: 1, rateLimited: true, retryAfterSeconds: nil, jitter: 0
        ), 30)
        XCTAssertEqual(FirebaseV2BootstrapRetryPolicy.delaySeconds(
            failureCount: 1, rateLimited: false, retryAfterSeconds: nil, jitter: 0.5
        ), 5)
        XCTAssertEqual(FirebaseV2BootstrapRetryPolicy.delaySeconds(
            failureCount: 3, rateLimited: false, retryAfterSeconds: nil, jitter: 0.5
        ), 20)
        XCTAssertEqual(FirebaseV2BootstrapRetryPolicy.delaySeconds(
            failureCount: 17, rateLimited: false, retryAfterSeconds: nil, jitter: 1
        ), 300)
        XCTAssertEqual(FirebaseV2BootstrapRetryPolicy.delaySeconds(
            failureCount: 17, rateLimited: false, retryAfterSeconds: 1_800, jitter: 0
        ), 1_800)
    }

    func testOnlyTransientFailuresTriggerStartupRetry() {
        XCTAssertTrue(FirebaseV2BootstrapRecoveryPolicy.isTransient(
            FirebaseV2BootstrapClientError.server(.rateLimited)
        ))
        XCTAssertTrue(FirebaseV2BootstrapRecoveryPolicy.isTransient(
            FirebaseV2BootstrapClientError.server(.grantNotConverged)
        ))
        XCTAssertFalse(FirebaseV2BootstrapRecoveryPolicy.isTransient(
            FirebaseV2BootstrapClientError.server(.rolloutDisabled)
        ))
        XCTAssertFalse(FirebaseV2BootstrapRecoveryPolicy.isTransient(
            FirebaseV2BootstrapClientError.server(.authenticationRequired)
        ))
    }

    func testThirtyMinuteLeaseRenewsWithSameSixtyToNinetySecondMargin() {
        for lead in [60_000, 75_000, 90_000] as [Int64] {
            XCTAssertEqual(RealtimeRolloutTiming.earlyRefreshDelayMilliseconds(
                serverDelay: 1_770_000, expiresIn: 1_800_000, lead: lead
            ), 1_800_000 - lead)
        }
        XCTAssertEqual(RealtimeRolloutTiming.earlyRefreshDelayMilliseconds(
            serverDelay: 20_000, expiresIn: 1_800_000, lead: 75_000
        ), 20_000)
    }

    func testEarlyRenewalUsesServerDeadlineOrSixtyToNinetySecondMargin() {
        XCTAssertEqual(RealtimeRolloutTiming.earlyRefreshDelayMilliseconds(
            serverDelay: 270_000, expiresIn: 300_000, lead: 75_000
        ), 225_000)
        XCTAssertEqual(RealtimeRolloutTiming.earlyRefreshDelayMilliseconds(
            serverDelay: 20_000, expiresIn: 300_000, lead: 75_000
        ), 20_000)
        XCTAssertEqual(RealtimeRolloutTiming.earlyRefreshDelayMilliseconds(
            serverDelay: 80_000, expiresIn: 100_000, lead: 90_000
        ), 50_000)
    }
}
