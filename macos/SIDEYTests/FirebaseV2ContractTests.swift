import XCTest
@testable import SIDEY

final class FirebaseV2ContractTests: XCTestCase {
    func testRevisionRequiresExactlyTwentyASCIIDigitsAndSortsLexically() throws {
        let earlier = try XCTUnwrap(RealtimeRevision(rawValue: "00000000000000000009"))
        let later = try XCTUnwrap(RealtimeRevision(rawValue: "00000000000000000010"))

        XCTAssertLessThan(earlier, later)
        XCTAssertNil(RealtimeRevision(rawValue: "9"))
        XCTAssertNil(RealtimeRevision(rawValue: "0000000000000000000a"))
        XCTAssertNil(RealtimeRevision(rawValue: "１２３４５６７８９０１２３４５６７８９０"))
    }

    func testBootstrapRequestOmitsAbsentBarrierAndEncodesExactRevision() throws {
        let empty = try XCTUnwrap(
            JSONSerialization.jsonObject(
                with: JSONEncoder().encode(FirebaseV2BootstrapRequest())
            ) as? [String: Any]
        )
        XCTAssertTrue(empty.isEmpty)

        let revision = try XCTUnwrap(RealtimeRevision(rawValue: "00000000000000000042"))
        let value = try XCTUnwrap(
            JSONSerialization.jsonObject(
                with: JSONEncoder().encode(
                    FirebaseV2BootstrapRequest(minimumAccessRevision: revision)
                )
            ) as? [String: String]
        )
        XCTAssertEqual(value, ["minimumAccessRevision": revision.rawValue])
    }

    func testBootstrapRequestRejectsExplicitNullOrInvalidRevision() {
        XCTAssertThrowsError(try JSONDecoder().decode(
            FirebaseV2BootstrapRequest.self,
            from: Data(#"{"minimumAccessRevision":null}"#.utf8)
        ))
        XCTAssertThrowsError(try JSONDecoder().decode(
            FirebaseV2BootstrapRequest.self,
            from: Data(#"{"minimumAccessRevision":"42"}"#.utf8)
        ))
    }

    func testBootstrapSuccessDecodesTypedCredentialRevisionRoomsAndWireCodes() throws {
        let roomID = UUID(uuidString: "11111111-1111-4111-8111-111111111111")!
        let json = """
        {
            "protocolVersion":2,
            "databaseURL":"https://sidey.asia-southeast1.firebasedatabase.app",
            "firebaseApiKey":"firebase-public-api-key",
            "customToken":"sensitive-custom-token",
            "permissionSync":"event-driven",
            "authTokenLifetimeSeconds":3600,
            "refreshAfter":1800000270000,
            "rolloutLeaseExpiresAt":1800000300000,
            "accessRevision":"00000000000000000042",
            "rooms":["11111111-1111-4111-8111-111111111111"],
            "wireItems":["0","7","999999"]
        }
        """

        let success = try JSONDecoder().decode(
            FirebaseV2BootstrapSuccess.self,
            from: Data(json.utf8)
        )

        XCTAssertEqual(success.protocolVersion, 2)
        XCTAssertEqual(success.rooms, [roomID])
        XCTAssertEqual(success.accessRevision.rawValue, "00000000000000000042")
        XCTAssertEqual(success.wireItems.map(\.rawValue), ["0", "7", "999999"])
        XCTAssertEqual(success.customToken.value, "sensitive-custom-token")
        XCTAssertFalse(success.customToken.description.contains("sensitive-custom-token"))
        XCTAssertEqual(success.refreshAfter, 1_800_000_270_000)
        XCTAssertEqual(success.rolloutLeaseExpiresAt, 1_800_000_300_000)

        let encoded = try XCTUnwrap(
            JSONSerialization.jsonObject(with: JSONEncoder().encode(success)) as? [String: Any]
        )
        XCTAssertEqual(Set(encoded.keys), [
            "protocolVersion",
            "databaseURL",
            "firebaseApiKey",
            "customToken",
            "permissionSync",
            "authTokenLifetimeSeconds",
            "refreshAfter",
            "rolloutLeaseExpiresAt",
            "accessRevision",
            "rooms",
            "wireItems"
        ])
        XCTAssertEqual((encoded["refreshAfter"] as? NSNumber)?.int64Value, 1_800_000_270_000)
        XCTAssertEqual(
            (encoded["rolloutLeaseExpiresAt"] as? NSNumber)?.int64Value,
            1_800_000_300_000
        )
    }

    func testBootstrapSuccessRequiresBothRolloutLeaseKeys() {
        let json = """
        {
            "protocolVersion":2,
            "databaseURL":"https://sidey.asia-southeast1.firebasedatabase.app",
            "firebaseApiKey":"firebase-public-api-key",
            "customToken":"token",
            "permissionSync":"event-driven",
            "authTokenLifetimeSeconds":3600,
            "accessRevision":"00000000000000000042",
            "rooms":[],
            "wireItems":[]
        }
        """
        XCTAssertThrowsError(try JSONDecoder().decode(
            FirebaseV2BootstrapSuccess.self,
            from: Data(json.utf8)
        ))
    }

    func testBootstrapSuccessRejectsNumericNonCanonicalOrOutOfRangeWireCodes() {
        for wireItems in [#"[7]"#, #"["07"]"#, #"["1000000"]"#] {
            let json = """
            {
                "protocolVersion":2,
                "databaseURL":"https://sidey.asia-southeast1.firebasedatabase.app",
                "firebaseApiKey":"firebase-public-api-key",
                "customToken":"token",
                "permissionSync":"event-driven",
                "authTokenLifetimeSeconds":3600,
                "refreshAfter":1800000270000,
                "rolloutLeaseExpiresAt":1800000300000,
                "accessRevision":"00000000000000000042",
                "rooms":[],
                "wireItems":\(wireItems)
            }
            """
            XCTAssertThrowsError(try JSONDecoder().decode(
                FirebaseV2BootstrapSuccess.self,
                from: Data(json.utf8)
            ))
        }
    }

    func testBootstrapHTTPFailuresHaveStableClassification() {
        XCTAssertEqual(FirebaseV2BootstrapFailure(statusCode: 400), .invalidArgument)
        XCTAssertEqual(FirebaseV2BootstrapFailure(statusCode: 401), .authenticationRequired)
        XCTAssertEqual(FirebaseV2BootstrapFailure(statusCode: 405), .methodNotAllowed)
        XCTAssertEqual(FirebaseV2BootstrapFailure(statusCode: 409), .grantNotConverged)
        XCTAssertEqual(FirebaseV2BootstrapFailure(statusCode: 429), .rateLimited)
        XCTAssertEqual(FirebaseV2BootstrapFailure(statusCode: 503), .unavailable)
        XCTAssertEqual(FirebaseV2BootstrapFailure(statusCode: 502), .unexpectedStatus(502))
    }

    func testCanonicalRealtimePathsUseLowercaseUUIDComponents() {
        let roomID = UUID(uuidString: "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA")!
        let userID = UUID(uuidString: "BBBBBBBB-BBBB-4BBB-8BBB-BBBBBBBBBBBB")!
        let sessionID = UUID(uuidString: "CCCCCCCC-CCCC-4CCC-8CCC-CCCCCCCCCCCC")!

        XCTAssertEqual(FirebaseV2Path.liveRoot, "/v2/l")
        XCTAssertEqual(FirebaseV2Path.inboxRoot, "/v2/n")
        XCTAssertEqual(
            FirebaseV2Path.typing(roomID: roomID, userID: userID, sessionID: sessionID),
            "/v2/l/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/t/" +
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb/cccccccc-cccc-4ccc-8ccc-cccccccccccc"
        )
        XCTAssertEqual(
            FirebaseV2Path.pulse(roomID: roomID, userID: userID),
            "/v2/l/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/c/" +
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
        )
        XCTAssertEqual(
            FirebaseV2Path.characterThrow(roomID: roomID, userID: userID),
            "/v2/l/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa/x/" +
                "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
        )
        XCTAssertEqual(
            FirebaseV2Path.inbox(userID: userID),
            "/v2/n/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
        )
    }

    func testThrowPayloadUsesDecimalStringWireCodeAndExactKeys() throws {
        let target = UUID(uuidString: "11111111-1111-4111-8111-111111111111")!
        let payload = FirebaseV2ThrowPayload(
            wireCode: try XCTUnwrap(FirebaseV2WireCode(rawValue: "7")),
            timestamp: try XCTUnwrap(FirebaseV2Timestamp(rawValue: 1_799_999_999_000)),
            targetUserID: target
        )

        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: JSONEncoder().encode(payload)) as? [String: Any]
        )
        XCTAssertEqual(Set(object.keys), ["k", "t", "u"])
        XCTAssertEqual(object["k"] as? String, "7")
        XCTAssertEqual(object["u"] as? String, target.uuidString)
        XCTAssertEqual((object["t"] as? NSNumber)?.int64Value, 1_799_999_999_000)
    }

    func testTypingAndPulsePayloadsAreScalarServerTimestamps() throws {
        let timestamp = try XCTUnwrap(FirebaseV2Timestamp(rawValue: 1_799_999_999_000))
        let typing = try JSONEncoder().encode(FirebaseV2TypingPayload(timestamp: timestamp))
        let pulse = try JSONEncoder().encode(FirebaseV2PulsePayload(timestamp: timestamp))

        XCTAssertEqual(String(decoding: typing, as: UTF8.self), "1799999999000")
        XCTAssertEqual(String(decoding: pulse, as: UTF8.self), "1799999999000")
    }

    func testTransientHighWaterBaselinesFirstSnapshotAndOnlyAnimatesNewFreshValues() throws {
        var tracker = FirebaseV2TransientHighWater()
        let baseline = try XCTUnwrap(FirebaseV2Timestamp(rawValue: 10_000))
        let fresh = try XCTUnwrap(FirebaseV2Timestamp(rawValue: 11_000))
        let expired = try XCTUnwrap(FirebaseV2Timestamp(rawValue: 20_000))

        XCTAssertEqual(
            tracker.observe(baseline, receivedAtMilliseconds: 10_100),
            .baseline
        )
        XCTAssertEqual(
            tracker.observe(baseline, receivedAtMilliseconds: 10_200),
            .duplicateOrStale
        )
        XCTAssertEqual(
            tracker.observe(fresh, receivedAtMilliseconds: 16_000),
            .animate
        )
        XCTAssertEqual(
            tracker.observe(expired, receivedAtMilliseconds: 25_001),
            .outsideFreshnessWindow
        )
        XCTAssertEqual(tracker.latestTimestamp, expired)
    }

    func testTransientHighWaterToleratesSmallFutureSkewButKeepsBoundedFreshnessAndDeduplication() throws {
        let baseline = try XCTUnwrap(FirebaseV2Timestamp(rawValue: 10_000))
        let event = try XCTUnwrap(FirebaseV2Timestamp(rawValue: 20_000))
        let cases: [(age: Int64, expected: FirebaseV2TransientObservation)] = [
            (-1_001, .outsideFreshnessWindow),
            (-1_000, .animate),
            (-51, .animate),
            (0, .animate),
            (5_000, .animate),
            (5_001, .outsideFreshnessWindow),
        ]

        for testCase in cases {
            var tracker = FirebaseV2TransientHighWater()
            XCTAssertEqual(
                tracker.observe(baseline, receivedAtMilliseconds: 10_100),
                .baseline
            )
            XCTAssertEqual(
                tracker.observe(event, receivedAtMilliseconds: event.rawValue + testCase.age),
                testCase.expected,
                "Event age: \(testCase.age) ms"
            )
            XCTAssertEqual(
                tracker.observe(event, receivedAtMilliseconds: 21_000),
                .duplicateOrStale,
                "An observed event must not replay when the clock catches up."
            )
            XCTAssertEqual(
                tracker.observe(baseline, receivedAtMilliseconds: 21_000),
                .duplicateOrStale
            )
        }
    }
}
