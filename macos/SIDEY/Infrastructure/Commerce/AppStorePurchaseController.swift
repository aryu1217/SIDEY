import Foundation
import StoreKit
import OSLog

@MainActor
final class AppStorePurchaseController {
    private let logger = Logger(subsystem: "app.sidey.desktop", category: "AppStoreProducts")
    private let verifierURL: URL?
    private var productsByID: [String: Product] = [:]
    private var updatesTask: Task<Void, Never>?

    init(verifierURL: URL? = AppStoreServiceEndpoint.resolve()) {
        self.verifierURL = verifierURL
    }

    deinit { updatesTask?.cancel() }

    func loadProducts() async throws -> [String: StorefrontProductMetadata] {
        let products = try await Product.products(for: CommerceCatalog.products.map(\.appStoreProductID))
        try Task.checkCancellation()
        let missingIDs = Set(CommerceCatalog.products.map(\.appStoreProductID))
            .subtracting(products.map(\.id)).sorted().joined(separator: ",")
        logger.notice("StoreKit returned \(products.count) products; unavailable IDs: \(missingIDs, privacy: .public)")
        productsByID = Dictionary(uniqueKeysWithValues: products.compactMap { product in
            CommerceCatalog.product(appStoreID: product.id).map { ($0.id, product) }
        })
        return Dictionary(uniqueKeysWithValues: productsByID.map { logicalID, product in
            (logicalID, StorefrontProductMetadata(
                logicalProductID: logicalID,
                appStoreProductID: product.id,
                displayName: product.displayName,
                description: product.description,
                displayPrice: product.displayPrice
            ))
        })
    }

    func purchase(productID: String, userID: UUID, accessToken: String) async throws -> Bool {
        if productsByID[productID] == nil { _ = try await loadProducts() }
        guard let product = productsByID[productID] else {
            throw AppStorePurchaseError.productUnavailable
        }
        let result = try await product.purchase(options: [.appAccountToken(userID)])
        switch result {
        case .success(let verification):
            try await submit(verification, accessToken: accessToken)
            return true
        case .pending:
            throw AppStorePurchaseError.pending
        case .userCancelled:
            return false
        @unknown default:
            throw AppStorePurchaseError.unknownResult
        }
    }

    func restore(accessToken: String) async throws {
        try await AppStore.sync()
        try await reconcileCurrentEntitlements(accessToken: accessToken)
    }

    func reconcileCurrentEntitlements(accessToken: String) async throws {
        for await verification in Transaction.currentEntitlements {
            try Task.checkCancellation()
            try await submit(verification, accessToken: accessToken)
        }
    }

    func startObserving(
        accessToken: @escaping @Sendable () async throws -> String,
        didChange: @escaping @MainActor () -> Void,
        didFail: @escaping @MainActor () -> Void
    ) {
        updatesTask?.cancel()
        updatesTask = Task { @MainActor [weak self] in
            guard let self else { return }
            for await verification in Transaction.updates {
                guard !Task.isCancelled else { return }
                do {
                    try await submit(
                        verification,
                        accessToken: try await accessToken()
                    )
                    didChange()
                } catch {
                    logger.error("StoreKit transaction update failed: \(error.localizedDescription, privacy: .private)")
                    didFail()
                }
            }
        }
    }

    func stopObserving() {
        updatesTask?.cancel()
        updatesTask = nil
    }

    private func submit(
        _ verification: VerificationResult<Transaction>,
        accessToken: String
    ) async throws {
        try Task.checkCancellation()
        guard case .verified(let transaction) = verification else {
            throw AppStorePurchaseError.unverifiedTransaction
        }
        guard transaction.productID.isEmpty == false,
              CommerceCatalog.product(appStoreID: transaction.productID) != nil
        else { throw AppStorePurchaseError.productUnavailable }
        guard let verifierURL else { throw AppStorePurchaseError.verifierNotConfigured }

        var request = URLRequest(
            url: verifierURL.appending(path: "v1/app-store/transactions")
        )
        request.httpMethod = "POST"
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(
            SignedTransactionRequest(signedTransactionInfo: verification.jwsRepresentation)
        )
        let (_, response) = try await URLSession.shared.data(for: request)
        try Task.checkCancellation()
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw AppStorePurchaseError.serverRejected
        }
        await transaction.finish()
    }
}

enum AppStoreServiceEndpoint {
    static func resolve(
        environment: [String: String] = ProcessInfo.processInfo.environment,
        bundle: Bundle = .main
    ) -> URL? {
#if DEBUG
        let raw = environment["SIDEY_APP_STORE_VERIFIER_URL"]
            ?? bundle.object(forInfoDictionaryKey: "SIDEYAppStoreVerifierURL") as? String
#else
        let raw = bundle.object(forInfoDictionaryKey: "SIDEYAppStoreVerifierURL") as? String
#endif
        guard let value = raw?.trimmingCharacters(in: .whitespacesAndNewlines),
              !value.isEmpty,
              let url = URL(string: value),
              url.host != nil,
              url.user == nil,
              url.password == nil,
              url.query == nil,
              url.fragment == nil,
              Self.isAllowed(url)
        else { return nil }
        return url
    }

    private static func isAllowed(_ url: URL) -> Bool {
        if url.scheme == "https" { return true }
#if DEBUG
        return url.scheme == "http"
            && ["localhost", "127.0.0.1", "::1"].contains(url.host?.lowercased())
#else
        return false
#endif
    }
}

private struct SignedTransactionRequest: Encodable {
    let signedTransactionInfo: String
}

enum AppStorePurchaseError: LocalizedError {
    case productUnavailable
    case pending
    case unknownResult
    case unverifiedTransaction
    case verifierNotConfigured
    case serverRejected

    var errorDescription: String? {
        switch self {
        case .productUnavailable: L10n.text("store.error.product_unavailable")
        case .pending: L10n.text("store.error.purchase_pending")
        case .unknownResult: L10n.text("store.error.unknown_result")
        case .unverifiedTransaction: L10n.text("store.error.unverified_transaction")
        case .verifierNotConfigured: L10n.text("store.error.verifier_not_configured")
        case .serverRejected: L10n.text("store.error.server_rejected")
        }
    }
}
