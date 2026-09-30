import Foundation
import XCTest
@testable import SIDEY

final class DrawEventTests: XCTestCase {
    func testInitialParticipantsIncludeOnlyOnlineMembersInTheCurrentRoom() {
        let onlineID = UUID()
        let typingID = UUID()
        let awayID = UUID()
        let offlineID = UUID()
        let reconnectingID = UUID()
        let room = Room(
            id: UUID(),
            name: "친구들",
            ownerID: onlineID,
            members: [
                RoomMember(userID: onlineID, nickname: "온라인", characterID: "pixel_hamster", presence: .online),
                RoomMember(userID: typingID, nickname: "입력 중", characterID: "pixel_cat", presence: .typing),
                RoomMember(userID: awayID, nickname: "자리 비움", characterID: "pixel_tree", presence: .away),
                RoomMember(userID: offlineID, nickname: "오프라인", characterID: "pixel_rabbit", presence: .offline),
                RoomMember(userID: reconnectingID, nickname: "재연결", characterID: "pixel_bear", presence: .reconnecting)
            ],
            inviteCodeHint: "AB••••"
        )

        XCTAssertEqual(
            DrawParticipantSelectionPolicy.initialIDs(in: room),
            Set([onlineID, typingID])
        )
        XCTAssertTrue(DrawParticipantSelectionPolicy.initialIDs(in: nil).isEmpty)
    }

    func testServerLimitErrorsMapToLocalizedDrawErrors() {
        for (code, expected) in [
            ("draw_active_limit_reached", SideyBackendError.drawActiveLimitReached),
            ("draw_rate_limit_reached", SideyBackendError.drawRateLimitReached)
        ] {
            let error = NSError(domain: "PostgREST", code: 400, userInfo: [
                NSLocalizedDescriptionKey: "RPC failed: \(code)"
            ])
            XCTAssertEqual(SideyBackendError.normalized(error), expected)
            XCTAssertNotEqual(expected.localizedDescription, SideyBackendError.remote(diagnostic: code).localizedDescription)
        }
    }

    func testServerEventDecodesPerViewerIconAndFixedResult() throws {
        let eventID = UUID()
        let roomID = UUID()
        let creatorID = UUID()
        let firstID = UUID()
        let secondID = UUID()
        let payload: [String: Any] = [
            "id": eventID.uuidString,
            "room_id": roomID.uuidString,
            "creator_id": creatorID.uuidString,
            "kind": "roulette",
            "participant_ids": [firstID.uuidString, secondID.uuidString],
            "ladder_labels": NSNull(),
            "result": ["winner_user_id": secondID.uuidString],
            "created_at": "2026-09-28T01:02:03.123456+00:00",
            "icon_expires_at": "2026-09-28T01:32:03.123456+00:00",
            "closed_at": NSNull(),
            "opened_at": "2026-09-28T01:05:00+00:00",
            "icon_visible": false
        ]
        let event = try JSONDecoder().decode(DrawEvent.self, from: JSONSerialization.data(withJSONObject: payload))

        XCTAssertEqual(event.id, eventID)
        XCTAssertEqual(event.result.winnerUserID, secondID)
        XCTAssertFalse(event.iconVisible)
        XCTAssertNotNil(event.openedAt)
        XCTAssertNotNil(event.iconExpiryDate)
    }

    func testRPCParametersUseServerNamesAndLadderResultKeepsAssignments() throws {
        let roomID = UUID()
        let firstID = UUID()
        let secondID = UUID()
        let parameters = CreateDrawEventParameters(
            roomID: roomID,
            kind: "ladder",
            participantIDs: [firstID, secondID],
            ladderLabels: ["커피", "디저트"]
        )
        let encoded = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(parameters)) as? [String: Any])
        XCTAssertEqual(encoded["p_room_id"] as? String, roomID.uuidString)
        XCTAssertEqual(encoded["p_kind"] as? String, "ladder")
        XCTAssertEqual((encoded["p_participants"] as? [String])?.count, 2)
        XCTAssertEqual(encoded["p_ladder_labels"] as? [String], ["커피", "디저트"])

        let result = DrawResult(
            winnerUserID: nil,
            assignments: [
                DrawAssignment(userID: firstID, label: "디저트"),
                DrawAssignment(userID: secondID, label: "커피")
            ]
        )
        XCTAssertEqual(result.assignments?.map(\.label), ["디저트", "커피"])
    }
}
