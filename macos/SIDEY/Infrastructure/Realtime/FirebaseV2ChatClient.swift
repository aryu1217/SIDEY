import FirebaseFunctions
import Foundation

struct FirebaseV2ChatRequest: Codable, Equatable, Sendable {
    let body: String
    let messageID: UUID
    let roomID: UUID

    private enum CodingKeys: String, CodingKey {
        case body = "b"
        case messageID = "i"
        case roomID = "r"
    }
}

struct FirebaseV2ChatResponse: Codable, Equatable, Sendable {
    let body: String
    let messageID: UUID
    let bubbleWireCode: FirebaseV2WireCode?
    let sequence: Int64
    let timestamp: FirebaseV2Timestamp

    private enum CodingKeys: String, CodingKey {
        case body = "b"
        case messageID = "i"
        case bubbleWireCode = "k"
        case sequence = "n"
        case timestamp = "t"
    }

    init(
        body: String,
        messageID: UUID,
        bubbleWireCode: FirebaseV2WireCode?,
        sequence: Int64,
        timestamp: FirebaseV2Timestamp
    ) {
        self.body = body
        self.messageID = messageID
        self.bubbleWireCode = bubbleWireCode
        self.sequence = sequence
        self.timestamp = timestamp
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        body = try container.decode(String.self, forKey: .body)
        messageID = try container.decode(UUID.self, forKey: .messageID)
        bubbleWireCode = try container.decodeIfPresent(
            FirebaseV2WireCode.self,
            forKey: .bubbleWireCode
        )
        let sequence = try container.decode(Int64.self, forKey: .sequence)
        guard (1...9_007_199_254_740_991).contains(sequence) else {
            throw DecodingError.dataCorruptedError(
                forKey: .sequence,
                in: container,
                debugDescription: "Chat sequence must be a positive JavaScript-safe integer."
            )
        }
        self.sequence = sequence
        timestamp = try container.decode(FirebaseV2Timestamp.self, forKey: .timestamp)
    }
}

protocol FirebaseV2ChatCalling: Sendable {
    func call(_ request: FirebaseV2ChatRequest) async throws -> FirebaseV2ChatResponse
}

protocol FirebaseV2CommittedMessageLookingUp: Sendable {
    func committedMessage(id: UUID, roomID: UUID) async throws -> ChatMessage?
}

final class FirebaseV2FunctionsChatCaller: @unchecked Sendable, FirebaseV2ChatCalling {
    private let callable: Callable<FirebaseV2ChatRequest, FirebaseV2ChatResponse>

    init(functions: Functions) {
        var callable: Callable<FirebaseV2ChatRequest, FirebaseV2ChatResponse> =
            functions.httpsCallable("sendRealtimeChat")
        callable.timeoutInterval = 30
        self.callable = callable
    }

    func call(_ request: FirebaseV2ChatRequest) async throws -> FirebaseV2ChatResponse {
        try await callable(request)
    }
}

enum FirebaseV2ChatErrorPolicy {
    static func isUnauthenticated(_ error: Error) -> Bool {
        let nsError = error as NSError
        return nsError.domain == FunctionsErrorDomain
            && nsError.code == FunctionsErrorCode.unauthenticated.rawValue
    }

    static func outcome(for error: Error) -> RealtimeChatOutcome {
        let nsError = error as NSError
        guard nsError.domain == FunctionsErrorDomain,
              let code = FunctionsErrorCode(rawValue: nsError.code) else {
            return .reconciliationPending
        }
        switch code {
        case .unauthenticated:
            return .definitelyRejected(
                message: L10n.text("firebase.chat.error.authentication_expired")
            )
        case .permissionDenied:
            return .definitelyRejected(
                message: L10n.text("firebase.chat.error.permission_denied")
            )
        case .resourceExhausted:
            return .definitelyRejected(
                message: L10n.text("firebase.chat.error.rate_limited")
            )
        case .failedPrecondition:
            return .definitelyRejected(
                message: L10n.text("firebase.chat.error.state_conflict")
            )
        case .invalidArgument:
            return .definitelyRejected(
                message: L10n.text("firebase.chat.error.invalid_message")
            )
        case .OK, .cancelled, .unknown, .deadlineExceeded, .notFound,
                .alreadyExists, .aborted, .outOfRange, .unimplemented,
                .internal, .unavailable, .dataLoss:
            return .reconciliationPending
        @unknown default:
            return .reconciliationPending
        }
    }
}

actor FirebaseV2ChatClient {
    private let caller: any FirebaseV2ChatCalling
    private let committedMessageLookup: (any FirebaseV2CommittedMessageLookingUp)?
    private let senderUserID: UUID
    private var bubbleCatalogIDByWireCode: [FirebaseV2WireCode: String]
    private let lookupAttemptDelays: [Duration]

    init(
        caller: any FirebaseV2ChatCalling,
        committedMessageLookup: (any FirebaseV2CommittedMessageLookingUp)? = nil,
        senderUserID: UUID,
        bubbleCatalogIDByWireCode: [FirebaseV2WireCode: String],
        lookupAttemptDelays: [Duration] = [.zero, .milliseconds(250), .seconds(1)]
    ) {
        self.caller = caller
        self.committedMessageLookup = committedMessageLookup
        self.senderUserID = senderUserID
        self.bubbleCatalogIDByWireCode = bubbleCatalogIDByWireCode
        self.lookupAttemptDelays = lookupAttemptDelays
    }

    func replaceBubbleMapping(_ mapping: [FirebaseV2WireCode: String]) {
        bubbleCatalogIDByWireCode = mapping
    }

    func send(
        roomID: UUID,
        body: String,
        messageID: UUID,
        onUnauthenticated: (@Sendable () async -> Void)? = nil
    ) async -> RealtimeChatOutcome {
        do {
            let response = try await caller.call(FirebaseV2ChatRequest(
                body: body,
                messageID: messageID,
                roomID: roomID
            ))
            guard response.messageID == messageID,
                  response.body == body else {
                return await reconcileAmbiguousCommit(
                    roomID: roomID,
                    body: body,
                    messageID: messageID
                )
            }
            if let code = response.bubbleWireCode,
               bubbleCatalogIDByWireCode[code] == nil {
                return await reconcileAmbiguousCommit(
                    roomID: roomID,
                    body: body,
                    messageID: messageID
                )
            }
            return .confirmed(ChatMessage(
                id: response.messageID,
                roomID: roomID,
                senderID: senderUserID,
                body: response.body,
                createdAt: Date(timeIntervalSince1970: TimeInterval(
                    response.timestamp.rawValue
                ) / 1_000),
                bubbleStyleID: response.bubbleWireCode.flatMap {
                    bubbleCatalogIDByWireCode[$0]
                }
            ))
        } catch {
            let classified = FirebaseV2ChatErrorPolicy.outcome(for: error)
            if FirebaseV2ChatErrorPolicy.isUnauthenticated(error) {
                await onUnauthenticated?()
            }
            guard classified == .reconciliationPending else { return classified }
            return await reconcileAmbiguousCommit(
                roomID: roomID,
                body: body,
                messageID: messageID
            )
        }
    }

    private func reconcileAmbiguousCommit(
        roomID: UUID,
        body: String,
        messageID: UUID
    ) async -> RealtimeChatOutcome {
        guard let committedMessageLookup else { return .reconciliationPending }
        for delay in lookupAttemptDelays {
            if delay > .zero {
                do {
                    try await Task.sleep(for: delay)
                } catch {
                    return .reconciliationPending
                }
            }
            do {
                if let message = try await committedMessageLookup.committedMessage(
                    id: messageID,
                    roomID: roomID
                ), message.id == messageID,
                   message.roomID == roomID,
                   message.senderID == senderUserID,
                   message.body == body {
                    return .confirmed(message)
                }
            } catch {
                // A failed lookup does not prove non-commit. Continue the
                // bounded lookup schedule and never resend the callable.
            }
        }
        return .reconciliationPending
    }
}

extension SideyBackend: FirebaseV2CommittedMessageLookingUp {
    func committedMessage(id: UUID, roomID: UUID) async throws -> ChatMessage? {
        try await message(id: id, roomID: roomID)
    }
}
