import AppKit
import SpriteKit
import XCTest
@testable import SIDEY

@MainActor
final class DrawGameSceneTests: XCTestCase {
    func testLadderRoutesReachEveryServerAssignedLabelForTwoThroughTwelvePlayers() {
        for count in 2...12 {
            let ids = (0..<count).map { UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", $0 + 1))! }
            let labels = (0..<count).map { "항목 \($0 % 3)" }
            // Repeated labels are intentional: a displayed slot is consumed once.
            let destinations = Array((0..<count).reversed())
            let result = DrawResult(winnerUserID: nil, assignments: ids.enumerated().map { index, id in
                DrawAssignment(userID: id, label: labels[destinations[index]])
            })
            let event = DrawEvent(
                id: UUID(uuidString: "12345678-0000-0000-0000-000000000001")!,
                roomID: UUID(), creatorID: ids[0], kind: .ladder,
                participantIDs: ids, ladderLabels: labels, result: result,
                createdAt: "2026-09-29T00:00:00Z",
                iconExpiresAt: "2026-09-29T00:30:00Z",
                closedAt: nil, openedAt: nil, iconVisible: true
            )
            let layout = DrawLadderLayout(event: event, participantIDs: ids)
            let replayLayout = DrawLadderLayout(event: event, participantIDs: ids)

            XCTAssertEqual(layout.rungs, replayLayout.rungs)
            XCTAssertGreaterThanOrEqual(layout.rungs.count, 4,
                                        "\(count)명 게임도 시작부터 충분한 교차선이 있어야 함")

            var lanes = Array(0..<count)
            for rung in layout.rungs.sorted(by: { $0.row < $1.row }) {
                lanes.swapAt(rung.leftLane, rung.leftLane + 1)
            }
            for (slot, participantIndex) in lanes.enumerated() {
                XCTAssertEqual(labels[slot], result.assignments?[participantIndex].label,
                               "\(count)명 중 \(participantIndex)번 참가자가 서버 결과와 다른 칸에 도착")
            }
            XCTAssertEqual(Set(layout.destinationForParticipant).count, count)
        }
    }

    func testGameCharacterNameplateStaysBelowSprite() throws {
        let member = RoomMember(userID: UUID(), nickname: "긴닉네임친구",
                                characterID: "pixel_hamster", presence: .online)
        let character = DrawCharacterNode(member: member)
        let sprite = try XCTUnwrap(character.children.compactMap { $0 as? SKSpriteNode }.first)
        let nickname = try XCTUnwrap(character.children.compactMap { $0 as? SKLabelNode }.first)
        XCTAssertLessThan(nickname.frame.maxY, sprite.frame.minY)
    }

    func testLadderExtraCrossingsPreserveDistinctServerAssignments() {
        for count in [2, 5, 12] {
            let ids = (1...count).map {
                UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", $0))!
            }
            let labels = (0..<count).map { "항목 \($0)" }
            let event = DrawEvent(
                id: UUID(uuidString: "12345678-0000-0000-0000-000000000001")!,
                roomID: UUID(), creatorID: ids[0], kind: .ladder,
                participantIDs: ids, ladderLabels: labels,
                result: DrawResult(winnerUserID: nil, assignments: ids.enumerated().map { index, id in
                    DrawAssignment(userID: id, label: labels[count - index - 1])
                }),
                createdAt: "2026-09-29T00:00:00Z", iconExpiresAt: "2026-09-29T00:30:00Z",
                closedAt: nil, openedAt: nil, iconVisible: true
            )
            let layout = DrawLadderLayout(event: event, participantIDs: ids)
            var order = Array(0..<count)
            for rung in layout.rungs.sorted(by: { $0.row < $1.row }) {
                order.swapAt(rung.leftLane, rung.leftLane + 1)
            }
            XCTAssertEqual(order, Array((0..<count).reversed()))
        }
    }

    func testLadderHidesConnectionsUntilPlaybackStarts() throws {
        let ids = (1...3).map { UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", $0))! }
        let members = ids.map { RoomMember(userID: $0, nickname: "친구", characterID: "pixel_hamster", presence: .online) }
        let room = Room(id: UUID(), name: "테스트", ownerID: ids[0], members: members, inviteCodeHint: "AB••••")
        let event = DrawEvent(id: UUID(), roomID: room.id, creatorID: ids[0], kind: .ladder,
                              participantIDs: ids, ladderLabels: ["가", "나", "다"],
                              result: DrawResult(winnerUserID: nil, assignments: ids.enumerated().map { index, id in
                                  DrawAssignment(userID: id, label: ["가", "나", "다"][index])
                              }),
                              createdAt: "2026-09-29T00:00:00Z", iconExpiresAt: "2026-09-29T00:30:00Z",
                              closedAt: nil, openedAt: nil, iconVisible: true)
        let scene = DrawLadderScene(event: event, room: room, size: CGSize(width: 480, height: 320))
        let connections = try XCTUnwrap(scene.childNode(withName: "ladder-rungs"))
        XCTAssertTrue(connections.isHidden)
        scene.setProgress(0.01)
        XCTAssertFalse(connections.isHidden)
        scene.setProgress(0)
        XCTAssertTrue(connections.isHidden)
    }

    func testPreparedMiddleAndCompletedScenesRenderWithTransparentCorners() throws {
        let ids = (1...4).map { UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", $0))! }
        let characters = ["pixel_hamster", "pixel_cat", "pixel_puppy", "pixel_rabbit"]
        let members = ids.enumerated().map { index, id in
            RoomMember(userID: id, nickname: "친구\(index + 1)",
                       characterID: characters[index], presence: .online)
        }
        let room = Room(id: UUID(), name: "테스트", ownerID: ids[0],
                        members: members, inviteCodeHint: "AB••••")
        let labels = ["밥", "밥", "커피", "산책"]
        let output = ProcessInfo.processInfo.environment["SIDEY_DRAW_SCENE_OUTPUT_DIR"].map {
            URL(fileURLWithPath: $0, isDirectory: true)
        }
        if let output { try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true) }

        for kind in DrawKind.allCases {
            let result = kind == .ladder
                ? DrawResult(winnerUserID: nil, assignments: ids.enumerated().map { index, id in
                    DrawAssignment(userID: id, label: labels[(index + 1) % ids.count])
                })
                : DrawResult(winnerUserID: ids[2], assignments: nil)
            let event = DrawEvent(
                id: UUID(uuidString: "12345678-0000-0000-0000-000000000001")!,
                roomID: room.id, creatorID: ids[0], kind: kind,
                participantIDs: ids, ladderLabels: kind == .ladder ? labels : nil,
                result: result, createdAt: "2026-09-29T00:00:00Z",
                iconExpiresAt: "2026-09-29T00:30:00Z", closedAt: nil,
                openedAt: nil, iconVisible: true
            )
            let size = CGSize(width: 820, height: 560)
            let scene: SKScene = switch kind {
            case .ladder: DrawLadderScene(event: event, room: room, size: size)
            case .lot: DrawLotScene(event: event, room: room, size: size)
            case .roulette: DrawRouletteScene(event: event, room: room, size: size)
            }
            let view = SKView(frame: CGRect(origin: .zero, size: size))
            PixelWorldRendererPolicy.apply(to: view)
            view.presentScene(scene)
            view.isPaused = true
            for (name, progress) in [("ready", CGFloat(0)), ("middle", 0.5), ("done", 1)] {
                switch scene {
                case let scene as DrawLadderScene: scene.setProgress(progress)
                case let scene as DrawLotScene: scene.setProgress(progress)
                case let scene as DrawRouletteScene: scene.setProgress(progress)
                default: XCTFail("장면 유형 없음")
                }
                let texture = try XCTUnwrap(view.texture(from: scene))
                let bitmap = NSBitmapImageRep(cgImage: texture.cgImage())
                let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                XCTAssertGreaterThan(png.count, 500, "\(kind) \(name)")
                let corner = try XCTUnwrap(bitmap.colorAt(x: 0, y: 0))
                XCTAssertLessThan(corner.alphaComponent, 0.05, "\(kind) \(name)")
                let attachment = XCTAttachment(data: png, uniformTypeIdentifier: "public.png")
                attachment.name = "draw-\(kind.rawValue)-\(name)"
                attachment.lifetime = .keepAlways
                add(attachment)
                if let output { try png.write(to: output.appending(path: "\(kind.rawValue)-\(name).png")) }
            }
            view.presentScene(nil)
        }
    }

    func testTwelveParticipantScenesFitACompactScreen() throws {
        let ids = (1...12).map { UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", $0))! }
        let characters = ["pixel_hamster", "pixel_cat", "pixel_puppy", "pixel_rabbit"]
        let members = ids.enumerated().map { index, id in
            RoomMember(userID: id, nickname: "친구\(index + 1)",
                       characterID: characters[index % characters.count], presence: .online)
        }
        let room = Room(id: UUID(), name: "테스트", ownerID: ids[0],
                        members: members, inviteCodeHint: "AB••••")
        let labels = (0..<12).map { "항목 \($0 % 3)" }
        let size = CGSize(width: 480, height: 320)
        for kind in DrawKind.allCases {
            let result = kind == .ladder
                ? DrawResult(winnerUserID: nil, assignments: ids.enumerated().map { index, id in
                    DrawAssignment(userID: id, label: labels[11 - index])
                })
                : DrawResult(winnerUserID: ids[7], assignments: nil)
            let event = DrawEvent(
                id: UUID(uuidString: "12345678-0000-0000-0000-000000000002")!,
                roomID: room.id, creatorID: ids[0], kind: kind,
                participantIDs: ids, ladderLabels: kind == .ladder ? labels : nil,
                result: result, createdAt: "2026-09-29T00:00:00Z",
                iconExpiresAt: "2026-09-29T00:30:00Z", closedAt: nil,
                openedAt: nil, iconVisible: true
            )
            let scene: SKScene = switch kind {
            case .ladder: DrawLadderScene(event: event, room: room, size: size)
            case .lot: DrawLotScene(event: event, room: room, size: size)
            case .roulette: DrawRouletteScene(event: event, room: room, size: size)
            }
            let view = SKView(frame: CGRect(origin: .zero, size: size))
            PixelWorldRendererPolicy.apply(to: view)
            view.presentScene(scene)
            view.isPaused = true
            switch scene {
            case let scene as DrawLadderScene: scene.setProgress(1)
            case let scene as DrawLotScene: scene.setProgress(1)
            case let scene as DrawRouletteScene: scene.setProgress(1)
            default: XCTFail("장면 유형 없음")
            }
            let texture = try XCTUnwrap(view.texture(from: scene))
            let bitmap = NSBitmapImageRep(cgImage: texture.cgImage())
            let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            XCTAssertGreaterThan(png.count, 500, "\(kind)")
            XCTAssertLessThan(try XCTUnwrap(bitmap.colorAt(x: 0, y: 0)).alphaComponent, 0.05)
            let attachment = XCTAttachment(data: png, uniformTypeIdentifier: "public.png")
            attachment.name = "draw-\(kind.rawValue)-compact-twelve"
            attachment.lifetime = .keepAlways
            add(attachment)
            view.presentScene(nil)
        }
    }

    func testServerWinnerOwnsTheRevealedPaperAndStopsUnderTheRoulettePointer() throws {
        let size = CGSize(width: 480, height: 320)
        for count in 2...12 {
            let ids = (1...count).map {
                UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", $0))!
            }
            let members = ids.enumerated().map { index, id in
                RoomMember(userID: id, nickname: "친구\(index + 1)",
                           characterID: "pixel_hamster", presence: .online)
            }
            let room = Room(id: UUID(), name: "테스트", ownerID: ids[0],
                            members: members, inviteCodeHint: "AB••••")
            let winnerIndex = count / 2
            func event(_ kind: DrawKind) -> DrawEvent {
                DrawEvent(id: UUID(uuidString: "12345678-0000-0000-0000-000000000003")!,
                          roomID: room.id, creatorID: ids[0], kind: kind,
                          participantIDs: ids, ladderLabels: nil,
                          result: DrawResult(winnerUserID: ids[winnerIndex], assignments: nil),
                          createdAt: "2026-09-29T00:00:00Z",
                          iconExpiresAt: "2026-09-29T00:30:00Z",
                          closedAt: nil, openedAt: nil, iconVisible: true)
            }

            let lot = DrawLotScene(event: event(.lot), room: room, size: size)
            let papers = lot.children
            XCTAssertEqual(papers.count, count)
            lot.setProgress(0.125) // Fully folded at 0.5 seconds.
            for paper in papers {
                let printedCharacter = try XCTUnwrap(paper.children.first { $0 is DrawCharacterNode })
                XCTAssertEqual(printedCharacter.alpha, 0, accuracy: 0.001)
            }
            lot.setProgress(1)
            XCTAssertEqual(papers[winnerIndex].alpha, 1, accuracy: 0.001)
            XCTAssertTrue(papers.enumerated().allSatisfy { index, paper in
                index == winnerIndex || paper.zPosition < papers[winnerIndex].zPosition
            })
            let revealedCharacter = try XCTUnwrap(
                papers[winnerIndex].children.first { $0 is DrawCharacterNode }
            )
            XCTAssertEqual(revealedCharacter.alpha, 1, accuracy: 0.001)

            let roulette = DrawRouletteScene(event: event(.roulette), room: room, size: size)
            let wheel = try XCTUnwrap(roulette.children.first)
            let characters = wheel.children.compactMap { $0 as? DrawCharacterNode }
            XCTAssertEqual(characters.count, count)
            roulette.setProgress(0.9999)
            let beforeStop = wheel.zRotation
            roulette.setProgress(1)
            XCTAssertLessThan(abs(wheel.zRotation - beforeStop), 0.01)
            let winnerPosition = wheel.convert(characters[winnerIndex].position, to: roulette)
            // Character positions are rounded to whole pixels for nearest-neighbor drawing.
            XCTAssertEqual(winnerPosition.x, size.width / 2, accuracy: 0.51)
            XCTAssertTrue(characters.enumerated().allSatisfy { index, character in
                index == winnerIndex ||
                    wheel.convert(character.position, to: roulette).y < winnerPosition.y
            })
        }
    }
}
