import FirebaseFunctions
import XCTest
@testable import SIDEY

final class FirebaseV2ChatClientTests: XCTestCase {
    func testRequestUsesFrozenCompactKeys() throws {
        let request = FirebaseV2ChatRequest(
            body: "hello",
            messageID: UUID(uuidString: "11111111-1111-4111-8111-111111111111")!,
            roomID: UUID(uuidString: "22222222-2222-4222-8222-222222222222")!
        )
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: JSONEncoder().encode(request)) as? [String: Any]
        )
        XCTAssertEqual(Set(object.keys), ["b", "i", "r"])
        XCTAssertEqual(object["b"] as? String, "hello")
    }

    func testConfirmedResponsePreservesUUIDAndMapsBubbleWireCode() async throws {
        let messageID = UUID(uuidString: "11111111-1111-4111-8111-111111111111")!
        let roomID = UUID(uuidString: "22222222-2222-4222-8222-222222222222")!
        let senderID = UUID(uuidString: "33333333-3333-4333-8333-333333333333")!
        let wireCode = try XCTUnwrap(FirebaseV2WireCode(rawValue: "7"))
        let caller = FirebaseV2ChatCallerStub(result: .success(FirebaseV2ChatResponse(
            body: "hello",
            messageID: messageID,
            bubbleWireCode: wireCode,
            sequence: 8,
            timestamp: try XCTUnwrap(FirebaseV2Timestamp(rawValue: 1_800_000_000_000))
        )))
        let client = FirebaseV2ChatClient(
            caller: caller,
            senderUserID: senderID,
            bubbleCatalogIDByWireCode: [wireCode: "bubble_cloud"]
        )

        let outcome = await client.send(roomID: roomID, body: "hello", messageID: messageID)
        guard case .confirmed(let message) = outcome else {
            return XCTFail("expected confirmed message")
        }
        XCTAssertEqual(message.id, messageID)
        XCTAssertEqual(message.roomID, roomID)
        XCTAssertEqual(message.senderID, senderID)
        XCTAssertEqual(message.bubbleStyleID, "bubble_cloud")
        XCTAssertEqual(message.createdAt.timeIntervalSince1970, 1_800_000_000, accuracy: 0.001)
    }

    func testUnknownWireCodeAndMismatchedResponseStayPendingForReconciliation() async throws {
        let messageID = UUID()
        let unknownCode = try XCTUnwrap(FirebaseV2WireCode(rawValue: "77"))
        let caller = FirebaseV2ChatCallerStub(result: .success(FirebaseV2ChatResponse(
            body: "different",
            messageID: messageID,
            bubbleWireCode: unknownCode,
            sequence: 1,
            timestamp: try XCTUnwrap(FirebaseV2Timestamp(rawValue: 1))
        )))
        let client = FirebaseV2ChatClient(
            caller: caller,
            senderUserID: UUID(),
            bubbleCatalogIDByWireCode: [:]
        )

        let outcome = await client.send(
            roomID: UUID(),
            body: "original",
            messageID: messageID
        )
        XCTAssertEqual(outcome, .reconciliationPending)
    }

    func testOnlyContractGuaranteedNonCommitFunctionCodesBecomeRejected() {
        let definite: [FunctionsErrorCode] = [
            .unauthenticated, .permissionDenied, .resourceExhausted,
            .failedPrecondition, .invalidArgument,
        ]
        for code in definite {
            let result = FirebaseV2ChatErrorPolicy.outcome(for: NSError(
                domain: FunctionsErrorDomain,
                code: code.rawValue,
                userInfo: [NSLocalizedDescriptionKey: "must not be reflected"]
            ))
            guard case .definitelyRejected(let message) = result else {
                return XCTFail("\(code) must be a terminal non-commit reject")
            }
            XCTAssertFalse(message.contains("must not be reflected"))
        }

        for code in [FunctionsErrorCode.deadlineExceeded, .internal, .unavailable, .unknown] {
            XCTAssertEqual(
                FirebaseV2ChatErrorPolicy.outcome(for: NSError(
                    domain: FunctionsErrorDomain,
                    code: code.rawValue
                )),
                .reconciliationPending
            )
        }
        XCTAssertEqual(
            FirebaseV2ChatErrorPolicy.outcome(for: URLError(.networkConnectionLost)),
            .reconciliationPending
        )
    }

    func testUnauthenticatedNotifiesOnceWithoutResendingOrLookingUpMessage() async {
        let caller = FirebaseV2ChatCallerStub(result: .failure(NSError(
            domain: FunctionsErrorDomain,
            code: FunctionsErrorCode.unauthenticated.rawValue
        )))
        let lookup = FirebaseV2CommittedMessageLookupStub(results: [])
        let notification = FirebaseV2ChatAuthenticationNotificationStub()
        let client = FirebaseV2ChatClient(
            caller: caller,
            committedMessageLookup: lookup,
            senderUserID: UUID(),
            bubbleCatalogIDByWireCode: [:]
        )

        let outcome = await client.send(
            roomID: UUID(),
            body: "hello",
            messageID: UUID(),
            onUnauthenticated: { await notification.record() }
        )

        XCTAssertEqual(outcome, .definitelyRejected(
            message: L10n.text("firebase.chat.error.authentication_expired")
        ))
        let notificationCount = await notification.count
        let callCount = await caller.callCount
        let lookupCount = await lookup.requestCount
        XCTAssertEqual(notificationCount, 1)
        XCTAssertEqual(callCount, 1)
        XCTAssertEqual(lookupCount, 0)
    }

    func testOtherFunctionFailureDoesNotNotifyAuthenticationRecovery() async {
        let caller = FirebaseV2ChatCallerStub(result: .failure(NSError(
            domain: FunctionsErrorDomain,
            code: FunctionsErrorCode.permissionDenied.rawValue
        )))
        let notification = FirebaseV2ChatAuthenticationNotificationStub()
        let client = FirebaseV2ChatClient(
            caller: caller,
            senderUserID: UUID(),
            bubbleCatalogIDByWireCode: [:]
        )

        let outcome = await client.send(
            roomID: UUID(),
            body: "hello",
            messageID: UUID(),
            onUnauthenticated: { await notification.record() }
        )

        XCTAssertEqual(outcome, .definitelyRejected(
            message: L10n.text("firebase.chat.error.permission_denied")
        ))
        let notificationCount = await notification.count
        XCTAssertEqual(notificationCount, 0)
    }

    func testResponseRejectsUnsafeSequence() {
        let json = #"{"b":"x","i":"11111111-1111-4111-8111-111111111111","n":9007199254740992,"t":1}"#
        XCTAssertThrowsError(try JSONDecoder().decode(
            FirebaseV2ChatResponse.self,
            from: Data(json.utf8)
        ))
    }

    func testAmbiguousCallableFailureUsesSameUUIDLookupWithoutResend() async throws {
        let messageID = UUID(uuidString: "11111111-1111-4111-8111-111111111111")!
        let roomID = UUID(uuidString: "22222222-2222-4222-8222-222222222222")!
        let senderID = UUID(uuidString: "33333333-3333-4333-8333-333333333333")!
        let committed = ChatMessage(
            id: messageID,
            roomID: roomID,
            senderID: senderID,
            body: "hello",
            createdAt: Date(timeIntervalSince1970: 1_800_000_000),
            bubbleStyleID: nil
        )
        let caller = FirebaseV2ChatCallerStub(result: .failure(URLError(.timedOut)))
        let lookup = FirebaseV2CommittedMessageLookupStub(results: [.success(committed)])
        let client = FirebaseV2ChatClient(
            caller: caller,
            committedMessageLookup: lookup,
            senderUserID: senderID,
            bubbleCatalogIDByWireCode: [:],
            lookupAttemptDelays: [.zero]
        )

        let outcome = await client.send(roomID: roomID, body: "hello", messageID: messageID)
        let callCount = await caller.callCount
        let requests = await lookup.requests
        XCTAssertEqual(outcome, .confirmed(committed))
        XCTAssertEqual(callCount, 1)
        XCTAssertEqual(requests, [
            FirebaseV2CommittedMessageLookupStub.Request(id: messageID, roomID: roomID),
        ])
    }

    func testAmbiguousLookupMissRemainsPendingAndNeverResends() async {
        let caller = FirebaseV2ChatCallerStub(result: .failure(URLError(.networkConnectionLost)))
        let lookup = FirebaseV2CommittedMessageLookupStub(results: [.success(nil), .success(nil)])
        let client = FirebaseV2ChatClient(
            caller: caller,
            committedMessageLookup: lookup,
            senderUserID: UUID(),
            bubbleCatalogIDByWireCode: [:],
            lookupAttemptDelays: [.zero, .zero]
        )

        let outcome = await client.send(roomID: UUID(), body: "hello", messageID: UUID())
        let callCount = await caller.callCount
        let requestCount = await lookup.requestCount
        XCTAssertEqual(outcome, .reconciliationPending)
        XCTAssertEqual(callCount, 1)
        XCTAssertEqual(requestCount, 2)
    }
}

private actor FirebaseV2ChatCallerStub: FirebaseV2ChatCalling {
    let result: Result<FirebaseV2ChatResponse, Error>
    private(set) var callCount = 0

    init(result: Result<FirebaseV2ChatResponse, Error>) {
        self.result = result
    }

    func call(_ request: FirebaseV2ChatRequest) async throws -> FirebaseV2ChatResponse {
        callCount += 1
        return try result.get()
    }
}

private actor FirebaseV2ChatAuthenticationNotificationStub {
    private(set) var count = 0

    func record() {
        count += 1
    }
}

private actor FirebaseV2CommittedMessageLookupStub: FirebaseV2CommittedMessageLookingUp {
    struct Request: Equatable, Sendable {
        let id: UUID
        let roomID: UUID
    }

    private var results: [Result<ChatMessage?, Error>]
    private(set) var requests: [Request] = []
    var requestCount: Int { requests.count }

    init(results: [Result<ChatMessage?, Error>]) {
        self.results = results
    }

    func committedMessage(id: UUID, roomID: UUID) async throws -> ChatMessage? {
        requests.append(Request(id: id, roomID: roomID))
        guard !results.isEmpty else { return nil }
        return try results.removeFirst().get()
    }
}
