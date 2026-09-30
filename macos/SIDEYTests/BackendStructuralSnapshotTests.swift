import Security
import XCTest
@testable import SIDEY

final class BackendStructuralSnapshotTests: XCTestCase {
    func testNotificationsDuringSlowSnapshotShareOneWorkerAndDiscardStaleResult() async throws {
        let loader = SuspendedSnapshotLoader()
        let backend = makeBackend(loader)
        await backend.scheduleStructuralSnapshot()
        try await waitUntil { await loader.calls == 1 }
        for _ in 0..<20 { await backend.scheduleStructuralSnapshot() }
        let active = await loader.maximumActive
        XCTAssertEqual(active, 1)
        await loader.release()
        var iterator = backend.events.makeAsyncIterator()
        guard case .snapshot(let snapshot) = await iterator.next() else {
            await backend.shutdown()
            return XCTFail("Expected refreshed snapshot")
        }
        XCTAssertEqual(snapshot.profile?.nickname, "fresh")
        let calls = await loader.calls
        let maximum = await loader.maximumActive
        XCTAssertEqual(calls, 2)
        XCTAssertEqual(maximum, 1)
        await backend.shutdown()
    }

    func testShutdownInvalidatesAnUncancellableSnapshotBeforeItCanPublish() async throws {
        let loader = SuspendedSnapshotLoader()
        let backend = makeBackend(loader)
        await backend.scheduleStructuralSnapshot()
        try await waitUntil { await loader.calls == 1 }
        let shutdown = Task { await backend.shutdown() }
        // shutdown acquires the same topology queue after immediately revoking
        // the generation. Let the in-flight HTTP stand-in complete afterwards.
        try await waitUntil { await backend.structuralSnapshotStatus().isShutDown }
        await loader.release()
        await shutdown.value
        var iterator = backend.events.makeAsyncIterator()
        while let event = await iterator.next() {
            if case .snapshot = event { XCTFail("Shutdown must not publish stale rows") }
        }
    }

    private func makeBackend(_ loader: SuspendedSnapshotLoader) -> SideyBackend {
        SideyBackend(configuration: RuntimeConfiguration(
            supabaseURL: URL(string: "http://127.0.0.1:1")!,
            supabasePublishableKey: "unit-test-key", realtimeTransportPreference: nil),
            keychain: KeychainStore(service: "structural-unit-test", session: KeychainAccessSession(
                security: MissingKeychainSecurity())), snapshotLoader: { await loader.load() })
    }

    private func waitUntil(_ condition: () async -> Bool) async throws {
        let deadline = ContinuousClock().now.advanced(by: .seconds(2))
        while !(await condition()), ContinuousClock().now < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        let ready = await condition()
        XCTAssertTrue(ready)
    }
}

private actor SuspendedSnapshotLoader {
    private(set) var calls = 0
    private(set) var maximumActive = 0
    private var active = 0
    private var continuation: CheckedContinuation<Void, Never>?

    func load() async -> BackendSnapshot {
        calls += 1
        let call = calls
        active += 1
        maximumActive = max(maximumActive, active)
        if call == 1 { await withCheckedContinuation { continuation = $0 } }
        active -= 1
        return BackendSnapshot(profile: Profile(id: UUID(), nickname: call == 1 ? "stale" : "fresh",
            characterID: "pixel_hamster"), rooms: [])
    }
    func release() { continuation?.resume(); continuation = nil }
}

private struct MissingKeychainSecurity: KeychainSecurityPerforming {
    func copyMatching(_ query: CFDictionary) -> (status: OSStatus, data: Data?) { (errSecItemNotFound, nil) }
    func update(_ query: CFDictionary, attributes: CFDictionary) -> OSStatus { errSecItemNotFound }
    func add(_ attributes: CFDictionary) -> OSStatus { errSecSuccess }
    func delete(_ query: CFDictionary) -> OSStatus { errSecSuccess }
}
