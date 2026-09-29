import XCTest
@testable import SIDEY

final class RuntimeConfigurationTests: XCTestCase {
    #if DEBUG
    func testLocalMembershipTestModeIsLimitedToFourIsolatedLoopbackClients() throws {
        var services: Set<String> = []
        var suites: Set<String> = []
        for slot in 1...4 {
            let environment = [
                "SIDEY_LOCAL_MEMBERSHIP_TEST": "1",
                "SIDEY_LOCAL_CLIENT_SLOT": String(slot),
                "SIDEY_SUPABASE_URL": "http://127.0.0.1:61321",
                "SIDEY_SUPABASE_PUBLISHABLE_KEY": "sb_publishable_local"
            ]
            let channel = AppReleaseChannel.resolve(environment: environment)
            XCTAssertEqual(channel, .localMembershipTest(slot: slot))
            XCTAssertTrue(channel.isLocalMembershipTest)
            XCTAssertFalse(channel.requiresAppleAuthentication)
            XCTAssertFalse(channel.storeAvailability.allowsCommerceActions)
            services.insert(channel.keychainService)
            suites.insert(try XCTUnwrap(channel.preferencesSuiteName))

            let configuration = try RuntimeConfiguration.resolve(
                releaseChannel: channel,
                environment: environment,
                bundleInfo: [:]
            )
            XCTAssertEqual(configuration.supabaseURL.absoluteString, "http://127.0.0.1:61321")
            XCTAssertEqual(configuration.realtimeTransportPreference, .legacySupabase)
            XCTAssertFalse(configuration.isProductionBackend)
        }
        XCTAssertEqual(services.count, 4)
        XCTAssertEqual(suites.count, 4)
        XCTAssertFalse(services.contains(AppReleaseChannel.appStore.keychainService))
        XCTAssertFalse(suites.contains(AppReleaseChannel.appStore.preferencesSuiteName ?? ""))
    }

    func testLocalMembershipTestModeRejectsInvalidSlotsAndNonLoopbackBackends() {
        let valid = [
            "SIDEY_LOCAL_MEMBERSHIP_TEST": "1",
            "SIDEY_LOCAL_CLIENT_SLOT": "1",
            "SIDEY_SUPABASE_URL": "http://127.0.0.1:61321",
            "SIDEY_SUPABASE_PUBLISHABLE_KEY": "sb_publishable_local"
        ]
        for slot in ["", "0", "5", "01", "missing"] {
            var environment = valid
            environment["SIDEY_LOCAL_CLIENT_SLOT"] = slot
            let channel = AppReleaseChannel.resolve(environment: environment)
            XCTAssertThrowsError(try RuntimeConfiguration.resolve(
                releaseChannel: channel,
                environment: environment,
                bundleInfo: [:]
            ))
        }
        for url in [
            "https://example.supabase.co", "https://whtejsviizgejauasqqt.supabase.co",
            "http://192.168.0.10:61321", "http://127.0.0.1",
            "http://127.0.0.1:61321/path", "http://user@127.0.0.1:61321"
        ] {
            var environment = valid
            environment["SIDEY_SUPABASE_URL"] = url
            XCTAssertThrowsError(try RuntimeConfiguration.resolve(
                releaseChannel: .localMembershipTest(slot: 1),
                environment: environment,
                bundleInfo: [:]
            ))
        }
        var firebase = valid
        firebase["SIDEY_REALTIME_TRANSPORT"] = "firebase-v2"
        XCTAssertThrowsError(try RuntimeConfiguration.resolve(
            releaseChannel: .localMembershipTest(slot: 1),
            environment: firebase,
            bundleInfo: [:]
        ))
        var secret = valid
        secret["SIDEY_SUPABASE_PUBLISHABLE_KEY"] = "sb_secret_do-not-use"
        XCTAssertThrowsError(try RuntimeConfiguration.resolve(
            releaseChannel: .localMembershipTest(slot: 1),
            environment: secret,
            bundleInfo: [:]
        ))
        XCTAssertTrue(AppReleaseChannel.resolve(environment: [:]).requiresAppleAuthentication)
    }
    #endif

    func testAcceptsCompleteHTTPSPublishableConfiguration() throws {
        let configuration = try RuntimeConfiguration.resolve(releaseChannel: .staging, environment: [
            "SIDEY_SUPABASE_URL": "https://example.supabase.co",
            "SIDEY_SUPABASE_PUBLISHABLE_KEY": "sb_publishable_public"
        ])

        XCTAssertEqual(configuration.supabaseURL.absoluteString, "https://example.supabase.co")
        XCTAssertEqual(configuration.supabasePublishableKey, "sb_publishable_public")
        XCTAssertFalse(configuration.backendFingerprint.isEmpty)
        XCTAssertNil(configuration.realtimeTransportPreference)
    }

    func testDevelopmentReadsExplicitRealtimeTransportPreference() throws {
        let configuration = try RuntimeConfiguration.resolve(releaseChannel: .staging, environment: [
            "SIDEY_SUPABASE_URL": "https://example.supabase.co",
            "SIDEY_SUPABASE_PUBLISHABLE_KEY": "sb_publishable_public",
            "SIDEY_REALTIME_TRANSPORT": "firebase-v2"
        ])

        XCTAssertEqual(configuration.realtimeTransportPreference, .firebaseV2)

        XCTAssertThrowsError(try RuntimeConfiguration.resolve(releaseChannel: .staging, environment: [
            "SIDEY_SUPABASE_URL": "https://example.supabase.co",
            "SIDEY_SUPABASE_PUBLISHABLE_KEY": "sb_publishable_public",
            "SIDEY_REALTIME_TRANSPORT": "automatic"
        ]))
    }

    func testRejectsPartialInsecureAndSecretConfiguration() {
        XCTAssertThrowsError(try RuntimeConfiguration.resolve(releaseChannel: .staging, environment: [
            "SIDEY_SUPABASE_URL": "https://example.supabase.co"
        ]))
        XCTAssertThrowsError(try RuntimeConfiguration.resolve(releaseChannel: .staging, environment: [
            "SIDEY_SUPABASE_URL": "http://example.supabase.co",
            "SIDEY_SUPABASE_PUBLISHABLE_KEY": "sb_publishable_public"
        ]))
        XCTAssertThrowsError(try RuntimeConfiguration.resolve(releaseChannel: .staging, environment: [
            "SIDEY_SUPABASE_URL": "https://example.supabase.co",
            "SIDEY_SUPABASE_PUBLISHABLE_KEY": "sb_secret_do-not-ship"
        ]))
        XCTAssertThrowsError(try RuntimeConfiguration.resolve(releaseChannel: .staging, environment: [
            "SIDEY_SUPABASE_URL": "https://example.supabase.co",
            "SIDEY_SUPABASE_PUBLISHABLE_KEY": "eyJhbGciOiJIUzI1NiJ9.eyJyb2xlIjoic2VydmljZV9yb2xlIn0.signature"
        ]))
    }

    func testAllowsHTTPOnlyForLoopbackDevelopment() throws {
        for host in ["localhost", "127.0.0.1", "[::1]"] {
            let configuration = try RuntimeConfiguration.resolve(releaseChannel: .staging, environment: [
                "SIDEY_SUPABASE_URL": "http://\(host):54321",
                "SIDEY_SUPABASE_PUBLISHABLE_KEY": "sb_publishable_local"
            ])
            XCTAssertEqual(configuration.supabaseURL.scheme, "http")
        }
        XCTAssertThrowsError(try RuntimeConfiguration.resolve(releaseChannel: .staging, environment: [
            "SIDEY_SUPABASE_URL": "http://192.168.0.10:54321",
            "SIDEY_SUPABASE_PUBLISHABLE_KEY": "sb_publishable_local"
        ]))
    }

    func testDevelopmentRejectsProductionAndMissingConfiguration() {
        XCTAssertThrowsError(try RuntimeConfiguration.resolve(
            releaseChannel: .staging,
            environment: [:],
            bundleInfo: [:]
        ))
        XCTAssertThrowsError(try RuntimeConfiguration.resolve(
            releaseChannel: .staging,
            environment: [
                "SIDEY_SUPABASE_URL": "https://whtejsviizgejauasqqt.supabase.co",
                "SIDEY_SUPABASE_PUBLISHABLE_KEY": "sb_publishable_public"
            ],
            bundleInfo: [:]
        ))
    }

    func testProductionIgnoresInjectedBackendConfiguration() throws {
        let configuration = try RuntimeConfiguration.resolve(
            releaseChannel: .appStore,
            environment: [
                "SIDEY_SUPABASE_URL": "https://attacker.example",
                "SIDEY_SUPABASE_PUBLISHABLE_KEY": "sb_secret_do-not-ship",
                "SIDEY_REALTIME_TRANSPORT": "firebase-v2"
            ],
            bundleInfo: [:]
        )

        XCTAssertTrue(configuration.isProductionBackend)
        XCTAssertNil(configuration.realtimeTransportPreference)
    }

    func testAppStoreIgnoresBundledRealtimeTransportPreference() throws {
        let configuration = try RuntimeConfiguration.resolve(
            releaseChannel: .appStore,
            environment: ["SIDEY_REALTIME_TRANSPORT": "legacy"],
            bundleInfo: ["SIDEYRealtimeTransport": "firebase-v2"]
        )

        XCTAssertNil(configuration.realtimeTransportPreference)
    }

    func testAppStoreUsesProductionBackend() throws {
        let configuration = try RuntimeConfiguration.resolve(
            releaseChannel: .appStore,
            environment: [:],
            bundleInfo: [:]
        )

        XCTAssertTrue(configuration.isProductionBackend)
    }

    func testAppStoreVerifierEndpointRequiresHTTPSExceptLocalhost() {
        XCTAssertEqual(
            AppStoreServiceEndpoint.resolve(environment: [
                "SIDEY_APP_STORE_VERIFIER_URL": "https://iap.sidey.app/"
            ])?.absoluteString,
            "https://iap.sidey.app/"
        )
        XCTAssertNil(AppStoreServiceEndpoint.resolve(environment: [
            "SIDEY_APP_STORE_VERIFIER_URL": "http://iap.sidey.app"
        ]))
        XCTAssertEqual(
            AppStoreServiceEndpoint.resolve(environment: [
                "SIDEY_APP_STORE_VERIFIER_URL": "http://localhost:8080"
            ])?.absoluteString,
            "http://localhost:8080"
        )
        XCTAssertNil(AppStoreServiceEndpoint.resolve(environment: [
            "SIDEY_APP_STORE_VERIFIER_URL": "https://user:password@iap.sidey.app"
        ]))
        XCTAssertNil(AppStoreServiceEndpoint.resolve(environment: [
            "SIDEY_APP_STORE_VERIFIER_URL": "https://iap.sidey.app?destination=other"
        ]))
        XCTAssertNil(AppStoreServiceEndpoint.resolve(environment: [
            "SIDEY_APP_STORE_VERIFIER_URL": "https://iap.sidey.app#other"
        ]))
    }
}
