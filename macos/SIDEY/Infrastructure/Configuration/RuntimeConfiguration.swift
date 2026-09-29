import CryptoKit
import Foundation

struct RuntimeConfiguration: Equatable, Sendable {
    static let productionHost = "whtejsviizgejauasqqt.supabase.co"
    let supabaseURL: URL
    let supabasePublishableKey: String
    let realtimeTransportPreference: RealtimeTransportKind?

    var backendFingerprint: String {
        let digest = SHA256.hash(data: Data(supabaseURL.absoluteString.utf8))
        return digest.prefix(8).map { String(format: "%02x", $0) }.joined()
    }

    static func resolve(
        releaseChannel: AppReleaseChannel = .resolve(),
        environment: [String: String] = ProcessInfo.processInfo.environment,
        bundleInfo: [String: Any] = Bundle.main.infoDictionary ?? [:]
    ) throws -> Self {
        let bundledRealtimeTransport = bundleInfo["SIDEYRealtimeTransport"] as? String
        // Production transport ownership is remote and authenticated. Neither
        // environment variables nor bundle metadata may bypass its cohort or
        // kill-switch decision.
        let configuredRealtimeTransport = releaseChannel == .appStore
            ? nil
            : environment["SIDEY_REALTIME_TRANSPORT"] ?? bundledRealtimeTransport
        let realtimeTransportPreference: RealtimeTransportKind?
        do {
            realtimeTransportPreference = try RealtimeTransportPreference.resolve(configuredRealtimeTransport)
        } catch {
            throw RuntimeConfigurationError.invalidRealtimeTransport
        }

        #if DEBUG
        if case .localMembershipTest(let slot) = releaseChannel {
            guard environment["SIDEY_LOCAL_MEMBERSHIP_TEST"] == "1",
                  (1...4).contains(slot),
                  environment["SIDEY_LOCAL_CLIENT_SLOT"] == String(slot),
                  let rawURL = environment["SIDEY_SUPABASE_URL"],
                  let url = URL(string: rawURL),
                  Self.isLocalMembershipBackendURL(url),
                  let key = environment["SIDEY_SUPABASE_PUBLISHABLE_KEY"]?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !key.isEmpty,
                  realtimeTransportPreference == nil || realtimeTransportPreference == .legacySupabase
            else { throw RuntimeConfigurationError.invalidLocalMembershipTest }
            guard !Self.looksLikeSecretKey(key) else {
                throw RuntimeConfigurationError.secretKeyNotAllowed
            }
            return Self(
                supabaseURL: url,
                supabasePublishableKey: key,
                realtimeTransportPreference: .legacySupabase
            )
        }
        #endif

        if releaseChannel == .appStore {
            return Self(
                supabaseURL: URL(string: "https://\(productionHost)")!,
                supabasePublishableKey: "sb_publishable_kkASOI4rRTX8Drob21hkCw_VwUex63Y",
                realtimeTransportPreference: realtimeTransportPreference
            )
        }

        let environmentURL = environment["SIDEY_SUPABASE_URL"]?.trimmingCharacters(in: .whitespacesAndNewlines)
        let environmentKey = environment["SIDEY_SUPABASE_PUBLISHABLE_KEY"]?.trimmingCharacters(in: .whitespacesAndNewlines)
        let bundledURL = (bundleInfo["SIDEYSupabaseURL"] as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let bundledKey = (bundleInfo["SIDEYSupabasePublishableKey"] as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines)

        if environmentURL != nil || environmentKey != nil {
            guard let rawURL = environmentURL, !rawURL.isEmpty,
                  let key = environmentKey, !key.isEmpty,
                  let url = URL(string: rawURL), Self.isAllowedBackendURL(url)
            else { throw RuntimeConfigurationError.incompleteEnvironment }
            guard !Self.looksLikeSecretKey(key) else { throw RuntimeConfigurationError.secretKeyNotAllowed }
            guard url.host?.lowercased() != Self.productionHost else {
                throw RuntimeConfigurationError.productionBackendNotAllowedInDevelopment
            }
            return Self(
                supabaseURL: url,
                supabasePublishableKey: key,
                realtimeTransportPreference: realtimeTransportPreference
            )
        }

        guard let rawURL = bundledURL, !rawURL.isEmpty,
              let key = bundledKey, !key.isEmpty,
              let url = URL(string: rawURL), Self.isAllowedBackendURL(url)
        else { throw RuntimeConfigurationError.missingDevelopmentConfiguration }
        guard !Self.looksLikeSecretKey(key) else { throw RuntimeConfigurationError.secretKeyNotAllowed }
        guard url.host?.lowercased() != Self.productionHost else {
            throw RuntimeConfigurationError.productionBackendNotAllowedInDevelopment
        }
        return Self(
            supabaseURL: url,
            supabasePublishableKey: key,
            realtimeTransportPreference: realtimeTransportPreference
        )
    }

    var isProductionBackend: Bool {
        supabaseURL.host?.lowercased() == Self.productionHost
    }

    private static func isAllowedBackendURL(_ url: URL) -> Bool {
        guard let scheme = url.scheme?.lowercased(), let host = url.host?.lowercased() else {
            return false
        }
        if scheme == "https" { return true }
        return scheme == "http" && ["localhost", "127.0.0.1", "::1"].contains(host)
    }

    #if DEBUG
    private static func isLocalMembershipBackendURL(_ url: URL) -> Bool {
        guard url.scheme?.lowercased() == "http",
              let host = url.host?.lowercased(),
              ["localhost", "127.0.0.1", "::1"].contains(host),
              url.port != nil,
              url.user == nil,
              url.password == nil,
              url.query == nil,
              url.fragment == nil
        else { return false }
        return url.path.isEmpty || url.path == "/"
    }
    #endif

    private static func looksLikeSecretKey(_ value: String) -> Bool {
        if value.hasPrefix("sb_secret_") || value.hasPrefix("service_role") {
            return true
        }
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return false }
        var encodedPayload = String(parts[1])
            .replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        encodedPayload += String(repeating: "=", count: (4 - encodedPayload.count % 4) % 4)
        guard let data = Data(base64Encoded: encodedPayload),
              let payload = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return false }
        return payload["role"] as? String == "service_role"
    }
}

enum RuntimeConfigurationError: LocalizedError, Equatable {
    case incompleteEnvironment
    case secretKeyNotAllowed
    case missingDevelopmentConfiguration
    case productionBackendNotAllowedInDevelopment
    case invalidRealtimeTransport
    case invalidLocalMembershipTest

    var errorDescription: String? {
        switch self {
        case .incompleteEnvironment:
            InternalL10n.text("configuration.error.incomplete_environment")
        case .secretKeyNotAllowed:
            InternalL10n.text("configuration.error.secret_key_not_allowed")
        case .missingDevelopmentConfiguration:
            InternalL10n.text("configuration.error.missing_development_configuration")
        case .productionBackendNotAllowedInDevelopment:
            InternalL10n.text("configuration.error.production_backend_in_development")
        case .invalidRealtimeTransport:
            InternalL10n.text("configuration.error.unsupported_realtime_protocol")
        case .invalidLocalMembershipTest:
            "Local membership test requires a Debug build, slot 1–4, loopback Supabase URL, and publishable key."
        }
    }
}
