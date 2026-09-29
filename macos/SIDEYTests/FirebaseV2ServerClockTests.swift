import XCTest
@testable import SIDEY

final class FirebaseV2ServerClockTests: XCTestCase {
    func testOffsetCorrectsLocalTimeInBothDirectionsAndCanBeReplaced() {
        var clock = FirebaseV2ServerClock()
        XCTAssertEqual(clock.estimatedTime(localMilliseconds: 10_000), 10_000)
        clock.offsetMilliseconds = 250
        XCTAssertEqual(clock.estimatedTime(localMilliseconds: 10_000), 10_250)
        clock.offsetMilliseconds = -5_000
        XCTAssertEqual(clock.estimatedTime(localMilliseconds: 10_000), 5_000)
        clock.offsetMilliseconds = 51
        XCTAssertEqual(clock.estimatedTime(localMilliseconds: 10_000), 10_051)
    }

    func testInvalidOffsetMetadataCannotBecomeAClockCorrection() {
        for value in [nil, NSNull(), "51", true, Double.nan, Double.infinity,
                      -Double.infinity, Double(Int64.max)] as [Any?] {
            XCTAssertNil(FirebaseV2ServerClock.offset(from: value))
        }
        XCTAssertEqual(FirebaseV2ServerClock.offset(from: NSNumber(value: 51)), 51)
        XCTAssertEqual(FirebaseV2ServerClock.offset(from: NSNumber(value: -51)), -51)
        XCTAssertEqual(FirebaseV2ServerClock.offset(from: NSNumber(value: 0)), 0)
    }

    func testOffsetOverflowFallsBackWithoutCrashing() {
        let clock = FirebaseV2ServerClock(offsetMilliseconds: .max)
        XCTAssertEqual(clock.estimatedTime(localMilliseconds: 10_000), 10_000)
    }
}
