import XCTest
@testable import SIDEY

final class FirebaseV2BootstrapClientTests: XCTestCase {
    private let nowMilliseconds: Int64 = 1_800_000_000_000

    func testBootstrapSendsBearerTokenAndEmptyJSONBody() async throws {
        let transport = FirebaseV2HTTPTransportStub(
            response: try successResponse()
        )
        let client = makeClient(transport: transport)

        let result = try await client.bootstrap(
            accessToken: "supabase-access-token",
            minimumAccessRevision: nil
        )

        XCTAssertEqual(result.protocolVersion, 2)
        let capturedRequest = await transport.lastRequest
        let request = try XCTUnwrap(capturedRequest)
        XCTAssertEqual(request.url, FirebaseV2Endpoint.bootstrap)
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer supabase-access-token")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Type"), "application/json")
        XCTAssertEqual(request.value(forHTTPHeaderField: "Accept"), "application/json")
        XCTAssertEqual(request.httpBody, Data("{}".utf8))
    }

    func testBootstrapEncodesMinimumRevisionExactly() async throws {
        let transport = FirebaseV2HTTPTransportStub(
            response: try successResponse()
        )
        let client = makeClient(transport: transport)
        let revision = try XCTUnwrap(RealtimeRevision(rawValue: "00000000000000000042"))

        _ = try await client.bootstrap(
            accessToken: "token",
            minimumAccessRevision: revision
        )

        let capturedRequest = await transport.lastRequest
        let request = try XCTUnwrap(capturedRequest)
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: try XCTUnwrap(request.httpBody)) as? [String: String]
        )
        XCTAssertEqual(object, ["minimumAccessRevision": revision.rawValue])
    }

    func testBootstrapClassifiesContractHTTPFailuresWithoutReflectingResponseBody() async throws {
        for (status, expected) in [
            (400, FirebaseV2BootstrapFailure.invalidArgument),
            (401, .authenticationRequired),
            (405, .methodNotAllowed),
            (409, .grantNotConverged),
            (429, .rateLimited),
            (503, .unavailable),
            (502, .unexpectedStatus(502)),
        ] {
            let transport = FirebaseV2HTTPTransportStub(
                response: try response(status: status, body: #"{"error":"sensitive-upstream-body"}"#)
            )
            let client = makeClient(transport: transport)

            do {
                _ = try await client.bootstrap(accessToken: "token")
                XCTFail("HTTP \(status) must fail")
            } catch let error as FirebaseV2BootstrapClientError {
                XCTAssertEqual(error, .server(expected))
                XCTAssertFalse(error.localizedDescription.contains("sensitive-upstream-body"))
            }
        }
    }

    func testBootstrapRejectsWrongProtocolOrDatabaseIdentity() async throws {
        for (body, expectedError) in [
            (
                successBody.replacingOccurrences(
                    of: #""protocolVersion":2"#,
                    with: #""protocolVersion":3"#
                ),
                FirebaseV2BootstrapClientError.unexpectedContract
            ),
            (
                successBody.replacingOccurrences(
                    of: FirebaseV2Configuration.databaseURL.absoluteString,
                    with: "https://wrong-project.firebaseio.com"
                ),
                .unexpectedContract
            ),
            (
                successBody.replacingOccurrences(
                    of: #""rolloutLeaseExpiresAt":1800000300000"#,
                    with: #""rolloutLeaseExpiresAt":1800000301001"#
                ),
                .malformedSuccess
            ),
        ] {
            let transport = FirebaseV2HTTPTransportStub(
                response: try response(status: 200, body: body)
            )
            let client = makeClient(transport: transport)

            do {
                _ = try await client.bootstrap(accessToken: "token")
                XCTFail("Mismatched bootstrap identity must fail closed")
            } catch let error as FirebaseV2BootstrapClientError {
                XCTAssertEqual(error, expectedError)
            }
        }
    }

    func testBootstrapRejectsEmptySupabaseAccessTokenBeforeNetwork() async throws {
        let transport = FirebaseV2HTTPTransportStub(
            response: try successResponse()
        )
        let client = makeClient(transport: transport)

        do {
            _ = try await client.bootstrap(accessToken: "")
            XCTFail("An empty access token must not create a request")
        } catch let error as FirebaseV2BootstrapClientError {
            XCTAssertEqual(error, .missingAccessToken)
        }
        let capturedRequest = await transport.lastRequest
        XCTAssertNil(capturedRequest)
    }

    func testBootstrapDistinguishesGrantConvergenceFromRolloutRevocation() async throws {
        for (body, expected) in [
            (#"{"error":"realtime_grant_not_converged"}"#, FirebaseV2BootstrapFailure.grantNotConverged),
            (#"{"error":"realtime_rollout_disabled"}"#, .rolloutDisabled),
        ] {
            let transport = FirebaseV2HTTPTransportStub(
                response: try response(status: 409, body: body)
            )
            do {
                _ = try await makeClient(transport: transport).bootstrap(accessToken: "token")
                XCTFail("409 must fail")
            } catch let error as FirebaseV2BootstrapClientError {
                XCTAssertEqual(error, .server(expected))
            }
        }
    }

    func testRateLimitDeadlineBlocksAnotherRequestForSameSession() async throws {
        let transport = FirebaseV2HTTPTransportStub(response: try response(
            status: 429,
            body: #"{"error":"realtime_bootstrap_rate_limited"}"#,
            headers: ["Retry-After": "60"]
        ))
        let gate = FirebaseV2BootstrapRetryGate()
        let client = FirebaseV2BootstrapClient(
            transport: transport,
            retryGate: gate,
            nowMilliseconds: { 1_800_000_000_000 }
        )
        let identity = RealtimeCredentialIdentity(accountID: UUID(), sessionID: UUID())
        do {
            _ = try await client.bootstrap(accessToken: "token", identity: identity)
            XCTFail("First request must be rate limited")
        } catch let error as FirebaseV2BootstrapClientError {
            XCTAssertEqual(error, .server(.rateLimited))
        }

        let repeated = Task {
            try await client.bootstrap(accessToken: "token", identity: identity)
        }
        // The server's 60-second minimum makes a 20-ms observation enough to
        // distinguish a blocked retry from an immediate second POST.
        try await Task.sleep(for: .milliseconds(20))
        let callCount = await transport.callCount
        XCTAssertEqual(callCount, 1)
        repeated.cancel()
        do {
            _ = try await repeated.value
            XCTFail("Cancelled waiting request must not send")
        } catch is CancellationError {
            // Expected.
        }
    }

    func testBootstrapAnchorsLeaseToServerDateWhenLocalClockIsSkewed() async throws {
        let serverNowMilliseconds: Int64 = 1_800_000_000_000
        let localNowMilliseconds: Int64 = serverNowMilliseconds - 3_600_000
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "EEE',' dd MMM yyyy HH':'mm':'ss z"
        let serverDate = formatter.string(from: Date(
            timeIntervalSince1970: TimeInterval(serverNowMilliseconds) / 1_000
        ))
        let transport = FirebaseV2HTTPTransportStub(response: try response(
            status: 200,
            body: successBody,
            headers: ["Content-Type": "application/json", "Date": serverDate]
        ))
        let client = FirebaseV2BootstrapClient(
            transport: transport,
            nowMilliseconds: { localNowMilliseconds }
        )

        let result = try await client.bootstrap(accessToken: "token")

        XCTAssertEqual(
            result.rolloutLeaseSchedule.refreshAfterMilliseconds,
            localNowMilliseconds + 270_000
        )
        XCTAssertEqual(
            result.rolloutLeaseSchedule.expiresAtMilliseconds,
            localNowMilliseconds + 300_000
        )
        XCTAssertEqual(result.rolloutLeaseExpiresAt, serverNowMilliseconds + 300_000)
    }

    func testBootstrapAcceptsLegacyAndThirtyMinuteLeaseWithServerDatePrecision() async throws {
        for lifetime in [300_000, 1_800_000, 1_801_000] as [Int64] {
            let transport = FirebaseV2HTTPTransportStub(response: try response(
                status: 200,
                body: leaseBody(lifetimeMilliseconds: lifetime)
            ))
            let result = try await makeClient(transport: transport).bootstrap(accessToken: "token")
            XCTAssertEqual(result.rolloutLeaseExpiresAt, nowMilliseconds + lifetime)
            XCTAssertEqual(result.rolloutLeaseSchedule.expiresAtMilliseconds, nowMilliseconds + lifetime)
        }
    }

    func testBootstrapRejectsExpiredAndOverThirtyMinuteLeases() async throws {
        for lifetime in [0, -1, 1_801_001] as [Int64] {
            let transport = FirebaseV2HTTPTransportStub(response: try response(
                status: 200,
                body: leaseBody(lifetimeMilliseconds: lifetime)
            ))
            do {
                _ = try await makeClient(transport: transport).bootstrap(accessToken: "token")
                XCTFail("Expired or overlong lease must fail closed: \(lifetime)")
            } catch let error as FirebaseV2BootstrapClientError {
                XCTAssertEqual(error, .unexpectedContract)
            }
        }
    }

    private func leaseBody(lifetimeMilliseconds: Int64) -> String {
        successBody
            .replacingOccurrences(of: "1800000270000", with: String(nowMilliseconds + lifetimeMilliseconds - 30_000))
            .replacingOccurrences(of: "1800000300000", with: String(nowMilliseconds + lifetimeMilliseconds))
    }

    private func successResponse() throws -> FirebaseV2HTTPResponse {
        try response(status: 200, body: successBody)
    }

    private func response(
        status: Int,
        body: String,
        headers: [String: String] = ["Content-Type": "application/json"]
    ) throws -> FirebaseV2HTTPResponse {
        let response = try XCTUnwrap(HTTPURLResponse(
            url: FirebaseV2Endpoint.bootstrap,
            statusCode: status,
            httpVersion: "HTTP/1.1",
            headerFields: headers
        ))
        return FirebaseV2HTTPResponse(data: Data(body.utf8), response: response)
    }


    private func makeClient(
        transport: any FirebaseV2HTTPTransport
    ) -> FirebaseV2BootstrapClient {
        FirebaseV2BootstrapClient(
            transport: transport,
            nowMilliseconds: { 1_800_000_000_000 }
        )
    }

    private var successBody: String {
        """
        {
            "protocolVersion":2,
            "databaseURL":"https://sidey.asia-southeast1.firebasedatabase.app",
            "firebaseApiKey":"public-firebase-api-key-value",
            "customToken":"sensitive-custom-token",
            "permissionSync":"event-driven",
            "authTokenLifetimeSeconds":3600,
            "refreshAfter":1800000270000,
            "rolloutLeaseExpiresAt":1800000300000,
            "accessRevision":"00000000000000000042",
            "rooms":[],
            "wireItems":["0","7"]
        }
        """
    }
}

private actor FirebaseV2HTTPTransportStub: FirebaseV2HTTPTransport {
    private(set) var lastRequest: URLRequest?
    private(set) var callCount = 0
    private let response: FirebaseV2HTTPResponse

    init(response: FirebaseV2HTTPResponse) {
        self.response = response
    }

    func data(for request: URLRequest) async throws -> FirebaseV2HTTPResponse {
        lastRequest = request
        callCount += 1
        return response
    }
}
