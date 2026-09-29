import Foundation

enum AppPresentation {
    static let displayName = Bundle.main.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String ?? "SIDEY"
}

// Staging changes backend isolation only; both modes use App Store authentication and commerce.
enum AppReleaseChannel: Equatable {
    case appStore
    case staging
    #if DEBUG
    case localMembershipTest(slot: Int)
    #endif

    static func resolve(
        from bundle: Bundle = .main,
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> AppReleaseChannel {
#if DEBUG
        if environment["SIDEY_LOCAL_MEMBERSHIP_TEST"] == "1" {
            // Keep an invalid slot in the test channel so configuration fails
            // closed instead of silently connecting to the App Store backend.
            return .localMembershipTest(slot: Int(environment["SIDEY_LOCAL_CLIENT_SLOT"] ?? "") ?? 0)
        }
        if environment["SIDEY_STAGING"] == "1" { return .staging }
#endif
        return .appStore
    }

    var storeAvailability: StoreAvailability {
        #if DEBUG
        if case .localMembershipTest = self { return .comingSoon }
        #endif
        return .appStore
    }
    var keychainService: String {
        switch self {
        case .appStore: "com.sidey.desktop.appstore"
        case .staging: "com.sidey.desktop.appstore.staging"
        #if DEBUG
        case .localMembershipTest(let slot): "com.sidey.desktop.local-membership.slot\(slot)"
        #endif
        }
    }
    var preferencesSuiteName: String? {
        switch self {
        case .appStore: "app.sidey.desktop.appstore"
        case .staging: "app.sidey.desktop.appstore.staging"
        #if DEBUG
        case .localMembershipTest(let slot): "app.sidey.desktop.local-membership.slot\(slot)"
        #endif
        }
    }
    var loginItemMode: LaunchAtLoginController.Mode { .mainApp }
    var requiresAppleAuthentication: Bool {
        #if DEBUG
        if case .localMembershipTest = self { return false }
        #endif
        return true
    }

    var isLocalMembershipTest: Bool {
        #if DEBUG
        if case .localMembershipTest = self { return true }
        #endif
        return false
    }
}
