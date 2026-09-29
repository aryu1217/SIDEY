import FirebaseDatabase
import Foundation

/// An estimate for transient freshness only. Authentication and rollout leases
/// retain their existing server-enforced and monotonic lifetime checks.
struct FirebaseV2ServerClock: Sendable {
    var offsetMilliseconds: Int64 = 0

    func estimatedTime(localMilliseconds: Int64) -> Int64 {
        let (result, overflow) = localMilliseconds.addingReportingOverflow(offsetMilliseconds)
        return overflow ? localMilliseconds : result
    }

    static func offset(from value: Any?) -> Int64? {
        guard let number = value as? NSNumber,
              CFGetTypeID(number) != CFBooleanGetTypeID(),
              number.doubleValue.isFinite else { return nil }
        return Int64(exactly: number.doubleValue.rounded())
    }
}

protocol FirebaseV2ServerTimeOffsetStreaming: Sendable {
    func offsets() -> AsyncStream<Int64>
}

/// RTDB updates this local metadata on connection/reconnection. It requires no
/// room read, backend write, polling, or change to the Mac's system clock.
final class FirebaseV2ServerTimeOffsetStream: @unchecked Sendable,
    FirebaseV2ServerTimeOffsetStreaming {
    private let database: Database

    init(database: Database) {
        self.database = database
    }

    func offsets() -> AsyncStream<Int64> {
        AsyncStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            let reference = database.reference(withPath: ".info/serverTimeOffset")
            let handle = reference.observe(.value) { snapshot in
                if let offset = FirebaseV2ServerClock.offset(from: snapshot.value) {
                    continuation.yield(offset)
                }
            } withCancel: { _ in
                continuation.finish()
            }
            let observer = FirebaseV2ObserverCancellation(reference: reference, handle: handle)
            continuation.onTermination = { @Sendable _ in observer.cancel() }
        }
    }
}
