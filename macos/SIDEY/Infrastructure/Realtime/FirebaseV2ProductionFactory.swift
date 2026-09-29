import Foundation

enum FirebaseV2ProductionContract {
    // Frozen backend/client fixture SHA-256. The server enables a session only
    // when registration supplies this exact value.
    static let hash = "0f2845d033df248b1745c6526c8c7100b8d8fa6839b45f28c73b1023053fce2e"
}

enum FirebaseV2ProductionFactoryError: LocalizedError, Equatable {
    case appVersionUnavailable
    case duplicateWireCode

    var errorDescription: String? {
        switch self {
        case .appVersionUnavailable:
            L10n.text("firebase.production.error.app_version_unavailable")
        case .duplicateWireCode:
            L10n.text("firebase.production.error.duplicate_wire_code")
        }
    }
}

struct FirebaseV2ProductionRuntime: Sendable {
    let router: RoomMessagingTransportRouter
    let rolloutMonitor: RealtimeRolloutMonitor?
    let initialRefreshInterval: Duration
}

actor RealtimeRolloutMonitor {
    private let backend: SideyBackend
    private let identity: RealtimeCredentialIdentity
    private let cache: KeychainRealtimeRolloutPolicyCache
    private let appVersion: String

    init(
        backend: SideyBackend,
        identity: RealtimeCredentialIdentity,
        cache: KeychainRealtimeRolloutPolicyCache,
        appVersion: String
    ) {
        self.backend = backend
        self.identity = identity
        self.cache = cache
        self.appVersion = appVersion
    }

    func refresh() async throws -> RealtimeRolloutPolicyDecision {
        try await RealtimeRolloutPolicyResolver.resolve(
            identity: identity,
            contractHash: FirebaseV2ProductionContract.hash,
            cache: cache,
            fetch: {
                try await self.backend.registerRealtimeCapabilityV2(
                    platform: RealtimeRolloutPolicyEvaluator.platform,
                    appVersion: self.appVersion,
                    protocolVersion: RealtimeRolloutPolicyEvaluator.protocolVersion,
                    contractHash: FirebaseV2ProductionContract.hash
                )
            }
        )
    }
}

@MainActor
enum FirebaseV2ProductionFactory {
    nonisolated static func appVersion(bundleInfo: [String: Any]) throws -> String {
        guard let marketingVersion = bundleInfo["CFBundleShortVersionString"] as? String,
              !marketingVersion.isEmpty,
              let buildVersion = bundleInfo["CFBundleVersion"] as? String,
              !buildVersion.isEmpty else {
            throw FirebaseV2ProductionFactoryError.appVersionUnavailable
        }
        return "\(marketingVersion)+\(buildVersion)"
    }

    static func makeRouter(
        backend: SideyBackend,
        configuration: RuntimeConfiguration,
        keychain: KeychainStore,
        bundle: Bundle = .main
    ) async throws -> FirebaseV2ProductionRuntime {
        let appVersion = try appVersion(bundleInfo: bundle.infoDictionary ?? [:])

        let supabaseSession = try await backend.currentFirebaseV2BootstrapSession()
        let cache = KeychainRealtimeRolloutPolicyCache(
            keychain: keychain,
            backendFingerprint: configuration.backendFingerprint
        )
        let decision = try await RealtimeRolloutPolicyResolver.resolve(
            identity: supabaseSession.identity,
            contractHash: FirebaseV2ProductionContract.hash,
            cache: cache,
            fetch: {
                try await backend.registerRealtimeCapabilityV2(
                    platform: RealtimeRolloutPolicyEvaluator.platform,
                    appVersion: appVersion,
                    protocolVersion: RealtimeRolloutPolicyEvaluator.protocolVersion,
                    contractHash: FirebaseV2ProductionContract.hash
                )
            }
        )
        let selection = RealtimeTransportSelection.resolve(
            requested: decision.state == .firebaseV2 ? .firebaseV2 : .legacySupabase,
            firebaseV2Allowed: decision.state == .firebaseV2
        )
        guard decision.state == .firebaseV2 else {
            let legacyAdapter = SideyBackendRealtimeAdapter(
                backend: backend,
                events: backend.events
            )
            let router = try RoomMessagingTransportRouter(
                selection: selection,
                makeLegacy: { legacyAdapter },
                shutdownShared: { await backend.shutdown() }
            )
            return FirebaseV2ProductionRuntime(
                router: router,
                rolloutMonitor: nil,
                initialRefreshInterval: .seconds(decision.cacheTTLSeconds)
            )
        }

        let wireItems = try await backend.firebaseV2StoreWireItems()
        let firebasePlane = SideyBackendRealtimeAdapter(
            backend: backend,
            events: backend.events
        )
        let firebaseTransport = try makeFirebaseTransport(
            backend: backend,
            supabasePlane: firebasePlane,
            accountID: supabaseSession.identity.accountID,
            wireItems: wireItems,
            bundle: bundle
        )
        let router = try RoomMessagingTransportRouter(
            selection: selection,
            makeFirebaseV2: { firebaseTransport },
            makeFirebaseV2ForRecovery: {
                let recoveredSession = try await backend.currentFirebaseV2BootstrapSession()
                let recoveredWireItems = try await backend.firebaseV2StoreWireItems()
                let recoveredPlane = SideyBackendRealtimeAdapter(
                    backend: backend,
                    events: await backend.subscribeEvents()
                )
                return try Self.makeFirebaseTransport(
                    backend: backend,
                    supabasePlane: recoveredPlane,
                    accountID: recoveredSession.identity.accountID,
                    wireItems: recoveredWireItems,
                    bundle: bundle
                )
            },
            makeLegacyForSwitch: {
                SideyBackendRealtimeAdapter(
                    backend: backend,
                    events: await backend.subscribeEvents()
                )
            },
            shutdownShared: { await backend.shutdown() }
        )
        return FirebaseV2ProductionRuntime(
            router: router,
            rolloutMonitor: RealtimeRolloutMonitor(
                backend: backend,
                identity: supabaseSession.identity,
                cache: cache,
                appVersion: appVersion
            ),
            initialRefreshInterval: .seconds(decision.cacheTTLSeconds)
        )
    }

    static func makeFirebaseTransport(
        backend: SideyBackend,
        supabasePlane: SideyBackendRealtimeAdapter,
        accountID: UUID,
        wireItems: [FirebaseV2StoreWireItem],
        bundle: Bundle = .main
    ) throws -> FirebaseV2CompositeTransport {
        var bubbles: [FirebaseV2WireCode: String] = [:]
        var throwables: [FirebaseV2WireCode: String] = [
            FirebaseV2WireCode(rawValue: "0")!: "patch_soft_ball"
        ]
        for item in wireItems {
            guard let code = item.wireCode else { continue }
            switch item.productKind {
            case .bubble:
                guard bubbles.updateValue(item.catalogItemID, forKey: code) == nil else {
                    throw FirebaseV2ProductionFactoryError.duplicateWireCode
                }
            case .throwable:
                guard throwables.updateValue(item.catalogItemID, forKey: code) == nil else {
                    throw FirebaseV2ProductionFactoryError.duplicateWireCode
                }
            case .character:
                continue
            }
        }

        let runtime = try FirebaseV2Runtime.configure(bundle: bundle)
        let credentials = FirebaseV2CompositeCredentialEstablisher(
            bootstrapClient: FirebaseV2BootstrapClient(),
            auth: runtime.auth,
            sessionProvider: { try await backend.currentFirebaseV2BootstrapSession() }
        )
        let chatClient = FirebaseV2ChatClient(
            caller: FirebaseV2FunctionsChatCaller(functions: runtime.functions),
            committedMessageLookup: backend,
            senderUserID: accountID,
            bubbleCatalogIDByWireCode: bubbles
        )
        let databaseValues = FirebaseV2DatabaseValueStream(database: runtime.database)
        return FirebaseV2CompositeTransport(
            credentials: credentials,
            supabasePlane: supabasePlane,
            databaseValues: databaseValues,
            databaseWrites: FirebaseV2DatabaseWriteAdapter(database: runtime.database),
            chatClient: chatClient,
            throwableCatalogIDByWireCode: throwables,
            serverTimeOffsets: FirebaseV2ServerTimeOffsetStream(database: runtime.database)
        )
    }
}
