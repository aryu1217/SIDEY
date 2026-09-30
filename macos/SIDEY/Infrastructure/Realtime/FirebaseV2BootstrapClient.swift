import Foundation

enum FirebaseV2Endpoint {
    static let bootstrap = URL(
        string: "https://asia-southeast1-sidey-realtime.cloudfunctions.net/bootstrapRealtime"
    )!
}

struct FirebaseV2HTTPResponse: @unchecked Sendable {
    let data: Data
    let response: HTTPURLResponse
}

protocol FirebaseV2HTTPTransport: Sendable {
    func data(for request: URLRequest) async throws -> FirebaseV2HTTPResponse
}

struct FirebaseV2URLSessionTransport: FirebaseV2HTTPTransport {
    private let session: URLSession

    init(session: URLSession = .shared) {
        self.session = session
    }

    func data(for request: URLRequest) async throws -> FirebaseV2HTTPResponse {
        let (data, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse else {
            throw FirebaseV2BootstrapClientError.invalidHTTPResponse
        }
        return FirebaseV2HTTPResponse(data: data, response: response)
    }
}

enum FirebaseV2BootstrapClientError: LocalizedError, Equatable {
    case missingAccessToken
    case invalidHTTPResponse
    case server(FirebaseV2BootstrapFailure)
    case malformedSuccess
    case unexpectedContract

    var errorDescription: String? {
        switch self {
        case .missingAccessToken:
            L10n.text("firebase.bootstrap.error.missing_access_token")
        case .invalidHTTPResponse:
            L10n.text("firebase.bootstrap.error.invalid_http_response")
        case .server(let failure):
            switch failure {
            case .invalidArgument:
                L10n.text("firebase.bootstrap.error.invalid_argument")
            case .authenticationRequired:
                L10n.text("firebase.bootstrap.error.authentication_required")
            case .methodNotAllowed:
                L10n.text("firebase.bootstrap.error.method_not_allowed")
            case .grantNotConverged:
                L10n.text("firebase.bootstrap.error.grant_not_converged")
            case .rolloutDisabled:
                L10n.text("firebase.bootstrap.error.rollout_disabled")
            case .rateLimited:
                L10n.text("firebase.bootstrap.error.rate_limited")
            case .unavailable, .unexpectedStatus:
                L10n.text("firebase.bootstrap.error.unavailable")
            }
        case .malformedSuccess:
            L10n.text("firebase.bootstrap.error.malformed_success")
        case .unexpectedContract:
            L10n.text("firebase.bootstrap.error.unexpected_contract")
        }
    }
}

struct FirebaseV2BootstrapClient: Sendable {
    private let transport: any FirebaseV2HTTPTransport
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder
    private let nowMilliseconds: @Sendable () -> Int64
    private let retryGate: FirebaseV2BootstrapRetryGate

    init(
        transport: any FirebaseV2HTTPTransport = FirebaseV2URLSessionTransport(),
        encoder: JSONEncoder = JSONEncoder(),
        decoder: JSONDecoder = JSONDecoder(),
        retryGate: FirebaseV2BootstrapRetryGate = .shared,
        nowMilliseconds: @escaping @Sendable () -> Int64 = {
            Int64(Date().timeIntervalSince1970 * 1_000)
        }
    ) {
        self.transport = transport
        self.encoder = encoder
        self.decoder = decoder
        self.retryGate = retryGate
        self.nowMilliseconds = nowMilliseconds
    }

    func bootstrap(
        accessToken: String,
        minimumAccessRevision: RealtimeRevision? = nil,
        identity: RealtimeCredentialIdentity? = nil
    ) async throws -> FirebaseV2BootstrapSuccess {
        guard !accessToken.isEmpty else {
            throw FirebaseV2BootstrapClientError.missingAccessToken
        }
        if let identity { try await retryGate.wait(for: identity) }

        var request = URLRequest(url: FirebaseV2Endpoint.bootstrap)
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.cachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.httpBody = try encoder.encode(
            FirebaseV2BootstrapRequest(minimumAccessRevision: minimumAccessRevision)
        )

        let result: FirebaseV2HTTPResponse
        do {
            result = try await transport.data(for: request)
        } catch {
            if let identity, !Task.isCancelled,
               !(error is CancellationError),
               (error as? URLError)?.code != .cancelled {
                await retryGate.failed(
                    for: identity,
                    rateLimited: false,
                    retryAfterSeconds: nil
                )
            }
            throw error
        }
        guard result.response.statusCode == 200 else {
            let errorCode = (try? decoder.decode(
                FirebaseV2BootstrapErrorResponse.self,
                from: result.data
            ))?.error
            let failure = FirebaseV2BootstrapFailure(
                statusCode: result.response.statusCode,
                errorCode: errorCode
            )
            if let identity {
                switch failure {
                case .rateLimited, .unavailable, .grantNotConverged:
                    await retryGate.failed(
                        for: identity,
                        rateLimited: failure == .rateLimited,
                        retryAfterSeconds: FirebaseV2BootstrapRetryPolicy.retryAfterSeconds(
                            from: result.response
                        )
                    )
                case .unexpectedStatus(let status) where (500...599).contains(status):
                    await retryGate.failed(
                        for: identity,
                        rateLimited: false,
                        retryAfterSeconds: FirebaseV2BootstrapRetryPolicy.retryAfterSeconds(
                            from: result.response
                        )
                    )
                default:
                    break
                }
            }
            throw FirebaseV2BootstrapClientError.server(failure)
        }

        var success: FirebaseV2BootstrapSuccess
        do {
            success = try decoder.decode(FirebaseV2BootstrapSuccess.self, from: result.data)
        } catch {
            throw FirebaseV2BootstrapClientError.malformedSuccess
        }

        let receivedAtMilliseconds = nowMilliseconds()
        let serverNowMilliseconds = result.response.firebaseV2ServerDateMilliseconds
            ?? receivedAtMilliseconds
        do {
            try success.anchorRolloutLeaseSchedule(
                receivedAtMilliseconds: receivedAtMilliseconds,
                serverNowMilliseconds: serverNowMilliseconds
            )
        } catch {
            throw FirebaseV2BootstrapClientError.unexpectedContract
        }
        guard success.protocolVersion == 2,
              success.databaseURL == FirebaseV2Configuration.databaseURL,
              success.permissionSync == .eventDriven,
              success.authTokenLifetimeSeconds > 0,
              !success.firebaseApiKey.isEmpty else {
            throw FirebaseV2BootstrapClientError.unexpectedContract
        }
        if let identity { await retryGate.succeeded(for: identity) }
        return success
    }
}

private struct FirebaseV2BootstrapErrorResponse: Decodable {
    let error: String
}

private extension HTTPURLResponse {
    var firebaseV2ServerDateMilliseconds: Int64? {
        guard let rawDate = value(forHTTPHeaderField: "Date") else { return nil }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "EEE',' dd MMM yyyy HH':'mm':'ss z"
        guard let date = formatter.date(from: rawDate) else { return nil }
        return Int64(date.timeIntervalSince1970 * 1_000)
    }
}
