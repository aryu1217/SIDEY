import XCTest
@testable import SIDEY

final class OverlayVisibilityStateTests: XCTestCase {
    func testOnlyConfirmedSuppressionOnEveryLiveSessionShowsBadge() {
        var state = OverlayVisibilityState()
        let room = UUID(), user = UUID(), hiddenID = UUID(), visibleID = UUID()
        state.merge([
            .init(userID: user, visibilityID: hiddenID, suppressed: true, revision: 1),
            .init(userID: user, visibilityID: visibleID, suppressed: false, revision: 2)
        ])
        XCTAssertFalse(state.isSuppressed(roomID: room, userID: user), "Stored settings alone are not live presence")
        state.update(roomID: room, joins: [user: [connection("hidden", hiddenID)]], leaves: [:], full: true)
        XCTAssertTrue(state.isSuppressed(roomID: room, userID: user))
        state.update(roomID: room, joins: [user: [connection("visible", visibleID)]], leaves: [:], full: false)
        XCTAssertFalse(state.isSuppressed(roomID: room, userID: user))
        state.update(roomID: room, joins: [:], leaves: [user: ["visible"]], full: false)
        XCTAssertTrue(state.isSuppressed(roomID: room, userID: user))
        XCTAssertEqual(state.basePresence(roomID: room, userID: user), .online, "One session leaving cannot take another offline")
        state.update(roomID: room, joins: [user: [connection("legacy", nil)]], leaves: [:], full: false)
        XCTAssertFalse(state.isSuppressed(roomID: room, userID: user), "Unknown clients may still display messages")
        state.resetRoom(room)
        XCTAssertFalse(state.isSuppressed(roomID: room, userID: user))
        XCTAssertEqual(state.basePresence(roomID: room, userID: user), .offline)
    }

    func testIdentityAndRoomIsolationAndStaleRevision() {
        var state = OverlayVisibilityState()
        let room = UUID(), otherRoom = UUID(), user = UUID(), otherUser = UUID(), id = UUID()
        state.merge([.init(userID: user, visibilityID: id, suppressed: true, revision: 10)])
        state.update(roomID: room, joins: [user: [connection("a", id)], otherUser: [connection("spoof", id)]], leaves: [:], full: true)
        XCTAssertTrue(state.isSuppressed(roomID: room, userID: user))
        XCTAssertFalse(state.isSuppressed(roomID: otherRoom, userID: user))
        XCTAssertFalse(state.isSuppressed(roomID: room, userID: otherUser))
        state.merge([.init(userID: user, visibilityID: id, suppressed: false, revision: 9)])
        XCTAssertTrue(state.isSuppressed(roomID: room, userID: user))
        state.merge([.init(userID: user, visibilityID: id, suppressed: false, revision: 11)])
        XCTAssertFalse(state.isSuppressed(roomID: room, userID: user))
    }

    func testSnapshotRemovesRevokedStatusWithoutOverwritingNewerBroadcast() {
        var state = OverlayVisibilityState()
        let room = UUID(), user = UUID(), id = UUID()
        state.merge([.init(userID: user, visibilityID: id, suppressed: true, revision: 10)])
        state.update(roomID: room, joins: [user: [connection("a", id)]], leaves: [:], full: true)
        state.reconcile([], startedAtRevision: 9)
        XCTAssertTrue(state.isSuppressed(roomID: room, userID: user))
        state.reconcile([], startedAtRevision: 10)
        XCTAssertFalse(state.isSuppressed(roomID: room, userID: user))
    }

    func testPresenceUpdateReplacesReferenceAndOfflineNeverShowsBadge() {
        var state = OverlayVisibilityState()
        let room = UUID(), user = UUID(), id = UUID()
        state.merge([.init(userID: user, visibilityID: id, suppressed: true, revision: 1)])
        state.update(roomID: room, joins: [user: [connection("legacy", nil)]], leaves: [:], full: true)
        state.update(roomID: room, joins: [user: [.init(reference: "new", previousReference: "legacy", visibilityID: id, state: .away)]], leaves: [:], full: false)
        XCTAssertTrue(state.isSuppressed(roomID: room, userID: user))
        state.update(roomID: room, joins: [user: [.init(reference: "offline", previousReference: "new", visibilityID: id, state: .offline)]], leaves: [:], full: false)
        XCTAssertFalse(state.isSuppressed(roomID: room, userID: user))
    }

    private func connection(_ ref: String, _ id: UUID?) -> OverlayVisibilityState.Connection {
        .init(reference: ref, previousReference: nil, visibilityID: id, state: .online)
    }

    func testRepeatedFiveRoomPresenceSnapshotsEmitOnlyChangedMembersAndRetainFullReplay() {
        var state = OverlayVisibilityState()
        let members = (0..<12).map { _ in
            RoomMember(userID: UUID(), nickname: "친구", characterID: "pixel_hamster", presence: .online)
        }
        let rooms = (0..<5).map { _ in Room(id: UUID(), name: "그룹", ownerID: members[0].userID,
            members: members, inviteCodeHint: "") }
        XCTAssertEqual(state.changes(rooms: rooms).count, 60)
        let user = members[0].userID, id = UUID(), room = rooms[0].id
        state.merge([.init(userID: user, visibilityID: id, suppressed: true, revision: 1)])
        state.update(roomID: room, joins: [user: [connection("a", id)]], leaves: [:], full: true)
        let changed = state.changes(rooms: rooms)
        XCTAssertEqual(changed.count, 1)
        guard case .overlaySuppression(let emittedRoom, let emittedUser, true) = changed.first else {
            return XCTFail("Expected the changed friend's suppression")
        }
        XCTAssertEqual(emittedRoom, room)
        XCTAssertEqual(emittedUser, user)
        for _ in 0..<100 {
            state.update(roomID: room, joins: [user: [connection("a", id)]], leaves: [:], full: true)
            XCTAssertTrue(state.changes(rooms: rooms).isEmpty)
        }
        XCTAssertEqual(state.events(rooms: rooms).count, 60, "A new/repaired subscriber needs the complete baseline")
        state.resetRoom(room)
        let disconnected = state.changes(rooms: rooms)
        XCTAssertEqual(disconnected.count, 1)
        guard case .overlaySuppression(_, _, false) = disconnected.first else {
            return XCTFail("Disconnect must remove the badge")
        }
    }
}

@MainActor
final class OverlayVisibilityPublisherTests: XCTestCase {
    @MainActor private final class Harness {
        var time: TimeInterval = 0
        var sent: [Bool] = []
        var error: Error?
        var hold: CheckedContinuation<Void, Never>?
        var entered: XCTestExpectation?
        lazy var publisher = OverlayVisibilityPublisher(now: { [unowned self] in time }, automaticallySchedule: false) { [unowned self] value in
            sent.append(value)
            if let entered { await withCheckedContinuation { hold = $0; entered.fulfill() } }
            if let error { throw error }
        }
        func advance(_ time: TimeInterval) async {
            self.time = time
            publisher.processDeadlines()
            await publisher.drain()
        }
    }

    func testRapidTogglesSendOnlyFinalValueAndReturningToConfirmedValueSendsNothing() async {
        let h = Harness()
        h.publisher.update(suppressed: false)
        await h.advance(0.5)
        for index in 0..<100 {
            h.time = 0.6 + Double(index) * 0.001
            h.publisher.update(suppressed: index.isMultiple(of: 2))
        }
        await h.advance(5)
        XCTAssertEqual(h.sent, [false])
        h.publisher.update(suppressed: true)
        await h.advance(5.49)
        XCTAssertEqual(h.sent, [false])
        await h.advance(5.5)
        XCTAssertEqual(h.sent, [false, true])
    }

    func testContinuousChangesCannotExceedMinimumIntervalAndKeepFinalValue() async {
        let h = Harness()
        h.publisher.update(suppressed: false)
        await h.advance(0.5)
        h.time = 0.6; h.publisher.update(suppressed: true)
        await h.advance(3.59)
        XCTAssertEqual(h.sent, [false])
        await h.advance(3.6)
        XCTAssertEqual(h.sent, [false, true])
    }

    func testRateRejectionIsRetriedAndStopCancelsPending() async {
        let h = Harness()
        h.error = OverlayVisibilityRateLimitError()
        h.publisher.update(suppressed: true)
        await h.advance(0.5)
        h.error = nil
        await h.advance(3.6)
        XCTAssertEqual(h.sent, [true, true])
        h.time = 4; h.publisher.update(suppressed: false)
        h.publisher.stop()
        await h.advance(20)
        XCTAssertEqual(h.sent, [true, true])

        let unavailable = Harness()
        unavailable.error = URLError(.notConnectedToInternet)
        unavailable.publisher.update(suppressed: true)
        await unavailable.advance(0.5)
        // Recovery explicitly reasserts the latest setting, replacing a delayed repair.
        unavailable.error = nil
        unavailable.publisher.reconnect(suppressed: false)
        await unavailable.advance(4)
        XCTAssertEqual(unavailable.sent, [true, false])
    }

    func testLostCommitResponseCannotKeepOppositeServerStateAfterUserReverts() async {
        let h = Harness()
        h.publisher.update(suppressed: false)
        await h.advance(0.5)
        h.time = 1; h.publisher.update(suppressed: true)
        h.error = URLError(.timedOut)
        h.entered = expectation(description: "Ambiguous hidden-state commit")
        h.time = 3.6; h.publisher.processDeadlines()
        await fulfillment(of: [h.entered!], timeout: 1)
        h.time = 4; h.publisher.update(suppressed: false)
        h.entered = nil; h.hold?.resume(); h.hold = nil
        await h.publisher.drain()
        h.error = nil
        await h.advance(19)
        XCTAssertEqual(h.sent, [false, true, false], "Old false confirmation must not hide an ambiguous true commit")
    }

    func testInFlightRequestNeverCreatesAnUnboundedToggleQueue() async {
        let h = Harness()
        h.entered = expectation(description: "One request in flight")
        h.publisher.update(suppressed: false)
        h.time = 0.5; h.publisher.processDeadlines()
        await fulfillment(of: [h.entered!], timeout: 1)
        for index in 0..<101 { h.publisher.update(suppressed: index.isMultiple(of: 2)) }
        XCTAssertEqual(h.sent, [false])
        h.entered = nil; h.hold?.resume(); h.hold = nil
        await h.publisher.drain()
        await h.advance(3.6)
        XCTAssertEqual(h.sent, [false, true])
    }
}
