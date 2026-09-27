import AppKit
import SpriteKit
import XCTest
@testable import SIDEY

@MainActor
final class PixelWorldTests: XCTestCase {
    func testStatusDotKeepsFivePointGapFromMeasuredNicknameFrame() {
        let shortFrame = CGRect(x: -12, y: 26, width: 24, height: 12)
        let longFrame = CGRect(x: -44, y: 26, width: 88, height: 12)
        let shortDot = PixelNameplateLayout.statusDotPosition(nicknameFrame: shortFrame)
        let longDot = PixelNameplateLayout.statusDotPosition(nicknameFrame: longFrame)

        XCTAssertEqual(
            shortDot.x + PixelNameplateLayout.statusDotRadius + PixelNameplateLayout.spacing,
            shortFrame.minX,
            accuracy: 0.001
        )
        XCTAssertEqual(
            longDot.x + PixelNameplateLayout.statusDotRadius + PixelNameplateLayout.spacing,
            longFrame.minX,
            accuracy: 0.001
        )
        XCTAssertLessThan(longDot.x, shortDot.x)
    }

    func testNameplateBackgroundAddsPaddingWithoutCoveringStatusDot() {
        let nicknameFrame = CGRect(x: -24, y: 26, width: 48, height: 12)
        let background = PixelNameplateLayout.backgroundFrame(nicknameFrame: nicknameFrame)
        let dot = PixelNameplateLayout.statusDotPosition(nicknameFrame: nicknameFrame)

        XCTAssertEqual(background.minX, nicknameFrame.minX - PixelNameplateLayout.horizontalPadding)
        XCTAssertEqual(background.maxX, nicknameFrame.maxX + PixelNameplateLayout.horizontalPadding)
        XCTAssertEqual(background.minY, nicknameFrame.minY - PixelNameplateLayout.verticalPadding)
        XCTAssertEqual(background.maxY, nicknameFrame.maxY + PixelNameplateLayout.verticalPadding)
        XCTAssertLessThanOrEqual(
            dot.x + PixelNameplateLayout.statusDotRadius,
            background.minX
        )
        XCTAssertEqual(PixelNameplateLayout.backgroundColor.alphaComponent, 0.62, accuracy: 0.001)
    }

    func testTwentyAgentsStayFiniteAndOnEveryEdgeTrackDuringLongSimulation() {
        for edge in OverlayEdge.allCases {
            let bounds = edge.isHorizontal
                ? CGRect(x: 0, y: 0, width: 1_200, height: 240)
                : CGRect(x: 0, y: 0, width: 240, height: 1_200)
            let geometry = EdgeTrackGeometry(bounds: bounds, edge: edge)
            let range = geometry.trackRange
            var agents = (0..<20).map { index in
                PixelMovementAgent(
                    id: UUID(),
                    trackPosition: range.lowerBound + CGFloat(index) * 10,
                    target: range.upperBound - CGFloat(index) * 9
                )
            }

            for _ in 0..<3_000 {
                PixelMovementSimulation.step(
                    agents: &agents,
                    deltaTime: 1.0 / 30.0,
                    geometry: geometry,
                    avoidanceRects: edge == .top
                        ? [CGRect(x: 380, y: 164, width: 440, height: 76)]
                        : [],
                    stoppedIDs: []
                )
            }

            for agent in agents {
                XCTAssertTrue(agent.trackPosition.isFinite)
                XCTAssertTrue(agent.velocity.isFinite)
                XCTAssertTrue(range.contains(agent.trackPosition))
                let point = geometry.point(for: agent.trackPosition)
                switch edge {
                case .bottom:
                    XCTAssertEqual(point.y, bounds.minY + EdgeTrackGeometry.footInset, accuracy: 0.001)
                case .top:
                    XCTAssertEqual(point.y, bounds.maxY - EdgeTrackGeometry.footInset, accuracy: 0.001)
                case .left:
                    XCTAssertEqual(point.x, bounds.minX + EdgeTrackGeometry.footInset, accuracy: 0.001)
                case .right:
                    XCTAssertEqual(point.x, bounds.maxX - EdgeTrackGeometry.footInset, accuracy: 0.001)
                }
            }
        }
    }

    func testComposerAvoidanceOccupiesOnlyTheTopCenterTrack() {
        let size = CGSize(width: 1_200, height: 240)

        XCTAssertEqual(
            PixelWorldAvoidanceLayout.composerRects(
                worldSize: size,
                edge: .top,
                composerVisible: true
            ),
            [CGRect(x: 380, y: 164, width: 440, height: 76)]
        )
        XCTAssertTrue(PixelWorldAvoidanceLayout.composerRects(
            worldSize: size,
            edge: .bottom,
            composerVisible: true
        ).isEmpty)
        XCTAssertTrue(PixelWorldAvoidanceLayout.composerRects(
            worldSize: size,
            edge: .top,
            composerVisible: false
        ).isEmpty)
    }

    func testAllEdgeFootPointsTouchTheScreenBoundary() {
        for edge in OverlayEdge.allCases {
            let bounds = edge.isHorizontal
                ? CGRect(x: 0, y: 0, width: 800, height: 240)
                : CGRect(x: 0, y: 0, width: 240, height: 800)
            let geometry = EdgeTrackGeometry(bounds: bounds, edge: edge)
            let foot = geometry.footPoint(for: geometry.trackRange.lowerBound)
            switch edge {
            case .bottom: XCTAssertEqual(foot.y, bounds.minY, accuracy: 0.001)
            case .top: XCTAssertEqual(foot.y, bounds.maxY, accuracy: 0.001)
            case .left: XCTAssertEqual(foot.x, bounds.minX, accuracy: 0.001)
            case .right: XCTAssertEqual(foot.x, bounds.maxX, accuracy: 0.001)
            }
        }
    }

    func testCrowdedTrackAllowsSafeOverlapWithoutNaN() {
        let geometry = EdgeTrackGeometry(
            bounds: CGRect(x: 0, y: 0, width: 48, height: 240),
            edge: .bottom
        )
        var agents = (0..<20).map { _ in
            PixelMovementAgent(id: UUID(), trackPosition: 24, target: 24)
        }

        for _ in 0..<300 {
            PixelMovementSimulation.step(
                agents: &agents,
                deltaTime: 1.0 / 30.0,
                geometry: geometry,
                avoidanceRects: [geometry.bounds],
                stoppedIDs: []
            )
        }

        XCTAssertTrue(agents.allSatisfy { $0.trackPosition.isFinite && $0.velocity.isFinite })
    }

    func testOverlappingHeadOnAgentsAccelerateThroughInsteadOfSlowing() {
        let geometry = EdgeTrackGeometry(
            bounds: CGRect(x: 0, y: 0, width: 320, height: 240),
            edge: .bottom
        )
        var agents = [
            PixelMovementAgent(id: UUID(), trackPosition: 120, velocity: 8, target: 280),
            PixelMovementAgent(id: UUID(), trackPosition: 160, velocity: -8, target: 40)
        ]

        PixelMovementSimulation.step(
            agents: &agents,
            deltaTime: 1.0 / 30.0,
            geometry: geometry,
            avoidanceRects: [],
            stoppedIDs: []
        )

        XCTAssertGreaterThan(agents[0].velocity, 8)
        XCTAssertLessThan(agents[1].velocity, -8)
        XCTAssertGreaterThan(agents[0].trackPosition, 120)
        XCTAssertLessThan(agents[1].trackPosition, 160)
    }

    func testOverlapEndsIdleSoCharactersDoNotRemainStacked() {
        let geometry = EdgeTrackGeometry(
            bounds: CGRect(x: 0, y: 0, width: 320, height: 240),
            edge: .bottom
        )
        var agents = [
            PixelMovementAgent(id: UUID(), trackPosition: 140, target: 280, idleRemaining: 1),
            PixelMovementAgent(id: UUID(), trackPosition: 145, target: 40, idleRemaining: 1)
        ]

        PixelMovementSimulation.step(
            agents: &agents,
            deltaTime: 1.0 / 30.0,
            geometry: geometry,
            avoidanceRects: [],
            stoppedIDs: []
        )

        XCTAssertTrue(agents.allSatisfy { $0.idleRemaining == 0 })
        XCTAssertNotEqual(agents[0].trackPosition, 140)
        XCTAssertNotEqual(agents[1].trackPosition, 145)
    }

    func testMessageBubbleOverlapSeparatesRapidlyOnEveryEdge() {
        for edge in OverlayEdge.allCases {
            let bounds = edge.isHorizontal
                ? CGRect(x: 0, y: 0, width: 640, height: 240)
                : CGRect(x: 0, y: 0, width: 240, height: 640)
            let geometry = EdgeTrackGeometry(bounds: bounds, edge: edge)
            var agents = [
                PixelMovementAgent(id: UUID(), trackPosition: 260, target: 560),
                PixelMovementAgent(id: UUID(), trackPosition: 330, target: 80)
            ]
            let initialGap = messageBubbleGap(agents: agents, halfWidth: 70)
            var reachedClearance = false

            for _ in 0..<30 {
                PixelMovementSimulation.step(
                    agents: &agents,
                    deltaTime: 1.0 / 30.0,
                    geometry: geometry,
                    avoidanceRects: [],
                    stoppedIDs: [],
                    messageBubbleTangentRanges: messageBubbleRanges(agents: agents, halfWidth: 70)
                )
                XCTAssertTrue(agents.allSatisfy {
                    $0.trackPosition.isFinite
                        && $0.velocity.isFinite
                        && geometry.trackRange.contains($0.trackPosition)
                        && abs($0.velocity) <= PixelMovementSimulation.messageBubbleMaximumSpeed
                })
                if messageBubbleGap(agents: agents, halfWidth: 70)
                    >= PixelMovementSimulation.messageBubbleClearance {
                    reachedClearance = true
                    break
                }
            }

            XCTAssertTrue(reachedClearance, "\(edge) did not clear message bubbles")
            XCTAssertGreaterThan(messageBubbleGap(agents: agents, halfWidth: 70), initialGap)
        }
    }

    func testMessageBubbleOverlapEndsIdleImmediately() {
        let geometry = EdgeTrackGeometry(
            bounds: CGRect(x: 0, y: 0, width: 520, height: 240),
            edge: .bottom
        )
        var agents = [
            PixelMovementAgent(id: UUID(), trackPosition: 220, target: 480, idleRemaining: 3),
            PixelMovementAgent(id: UUID(), trackPosition: 300, target: 40, idleRemaining: 3)
        ]

        PixelMovementSimulation.step(
            agents: &agents,
            deltaTime: 1.0 / 30.0,
            geometry: geometry,
            avoidanceRects: [],
            stoppedIDs: [],
            messageBubbleTangentRanges: messageBubbleRanges(agents: agents, halfWidth: 60)
        )

        XCTAssertTrue(agents.allSatisfy { $0.idleRemaining == 0 })
        XCTAssertLessThan(agents[0].trackPosition, 220)
        XCTAssertGreaterThan(agents[1].trackPosition, 300)
    }

    func testMessageBubbleSeparationTransfersForceFromStoppedOrEdgeBlockedCharacter() {
        let geometry = EdgeTrackGeometry(
            bounds: CGRect(x: 0, y: 0, width: 520, height: 240),
            edge: .bottom
        )
        let stoppedID = UUID()
        var stoppedPair = [
            PixelMovementAgent(id: stoppedID, trackPosition: 220, target: 480),
            PixelMovementAgent(id: UUID(), trackPosition: 270, target: 40)
        ]

        PixelMovementSimulation.step(
            agents: &stoppedPair,
            deltaTime: 1.0 / 30.0,
            geometry: geometry,
            avoidanceRects: [],
            stoppedIDs: [stoppedID],
            messageBubbleTangentRanges: messageBubbleRanges(agents: stoppedPair, halfWidth: 50)
        )

        XCTAssertEqual(stoppedPair[0].trackPosition, 220)
        XCTAssertEqual(stoppedPair[0].velocity, 0)
        XCTAssertGreaterThan(stoppedPair[1].trackPosition, 270)
        XCTAssertEqual(
            stoppedPair[1].velocity,
            PixelMovementSimulation.messageBubbleSeparationAcceleration * 2 / 30,
            accuracy: 0.001
        )

        var edgePair = [
            PixelMovementAgent(
                id: UUID(),
                trackPosition: geometry.trackRange.lowerBound,
                target: geometry.trackRange.upperBound
            ),
            PixelMovementAgent(id: UUID(), trackPosition: 80, target: geometry.trackRange.lowerBound)
        ]
        let blockedPosition = edgePair[0].trackPosition

        PixelMovementSimulation.step(
            agents: &edgePair,
            deltaTime: 1.0 / 30.0,
            geometry: geometry,
            avoidanceRects: [],
            stoppedIDs: [],
            messageBubbleTangentRanges: messageBubbleRanges(agents: edgePair, halfWidth: 50)
        )

        XCTAssertEqual(edgePair[0].trackPosition, blockedPosition)
        XCTAssertEqual(edgePair[0].velocity, 0)
        XCTAssertGreaterThan(edgePair[1].trackPosition, 80)
    }

    func testMessageBubbleOverlapStaysPutWhenBothCharactersCannotMoveOutward() {
        let geometry = EdgeTrackGeometry(
            bounds: CGRect(x: 0, y: 0, width: 100, height: 240),
            edge: .bottom
        )
        var agents = [
            PixelMovementAgent(
                id: UUID(),
                trackPosition: geometry.trackRange.lowerBound,
                target: geometry.trackRange.upperBound
            ),
            PixelMovementAgent(
                id: UUID(),
                trackPosition: geometry.trackRange.upperBound,
                target: geometry.trackRange.lowerBound
            )
        ]
        let original = agents

        PixelMovementSimulation.step(
            agents: &agents,
            deltaTime: 1.0 / 30.0,
            geometry: geometry,
            avoidanceRects: [],
            stoppedIDs: [],
            messageBubbleTangentRanges: messageBubbleRanges(agents: agents, halfWidth: 50)
        )

        XCTAssertEqual(agents.map(\.trackPosition), original.map(\.trackPosition))
        XCTAssertEqual(agents.map(\.velocity), [0, 0])
    }

    func testTypingBubblesDoNotCreateMessageCollisionRangesOrEndIdle() {
        let roomID = UUID()
        let members = [
            PixelWorldMember(
                id: UUID(), nickname: "첫째", characterID: "pixel_hamster",
                presence: .online, isTyping: true, isCurrentUser: true
            ),
            PixelWorldMember(
                id: UUID(), nickname: "둘째", characterID: "pixel_cat",
                presence: .online, isTyping: true, isCurrentUser: false
            )
        ]
        let scene = PixelWorldScene(size: CGSize(width: 520, height: 240))
        scene.apply(roomID: roomID, members: members, bubbles: [], edge: .bottom, installationSeed: 7)

        XCTAssertTrue(members.allSatisfy { scene.renderedBubbleIsTyping(for: $0.id) })
        let typingOnlyRanges = scene.messageBubbleTangentRanges
        XCTAssertTrue(typingOnlyRanges.isEmpty)

        scene.apply(
            roomID: roomID,
            members: members,
            bubbles: [ActiveBubble(
                senderID: members[0].id,
                messageID: UUID(),
                body: "실제 메시지",
                expiresAt: .now.addingTimeInterval(10)
            )],
            edge: .bottom,
            installationSeed: 7
        )
        XCTAssertEqual(Set(scene.messageBubbleTangentRanges.keys), [members[0].id])

        let geometry = EdgeTrackGeometry(bounds: scene.frame, edge: .bottom)
        var agents = [
            PixelMovementAgent(id: members[0].id, trackPosition: 180, target: 480, idleRemaining: 2),
            PixelMovementAgent(id: members[1].id, trackPosition: 260, target: 40, idleRemaining: 2)
        ]
        PixelMovementSimulation.step(
            agents: &agents,
            deltaTime: 1.0 / 30.0,
            geometry: geometry,
            avoidanceRects: [],
            stoppedIDs: [],
            messageBubbleTangentRanges: typingOnlyRanges
        )

        XCTAssertEqual(agents.map(\.trackPosition), [180, 260])
        XCTAssertTrue(agents.allSatisfy { $0.idleRemaining > 0 })
    }

    func testFourLongMessageBubblesOnNarrowTrackStayFiniteBoundedAndStable() {
        let geometry = EdgeTrackGeometry(
            bounds: CGRect(x: 0, y: 0, width: 140, height: 240),
            edge: .bottom
        )
        let ids = (1...4).map { value in
            UUID(uuidString: String(format: "00000000-0000-0000-0000-%012d", value))!
        }
        var agents = ids.map { PixelMovementAgent(id: $0, trackPosition: 70, target: 70) }
        var directionChanges = Dictionary(uniqueKeysWithValues: ids.map { ($0, 0) })
        var lastDirections = Dictionary(uniqueKeysWithValues: ids.map { ($0, CGFloat.zero) })
        var reachedMaximumSpeed = false

        for _ in 0..<300 {
            let previousPositions = Dictionary(uniqueKeysWithValues: agents.map { ($0.id, $0.trackPosition) })
            let ranges = Dictionary(uniqueKeysWithValues: agents.map { agent in
                let layout = PixelBubbleLayout.make(
                    text: String(repeating: "긴메시지", count: 50),
                    isTyping: false,
                    tangentPosition: agent.trackPosition,
                    tangentLength: geometry.tangentLength,
                    edge: .bottom
                )
                return (agent.id, layout.bodyTangentRange(at: agent.trackPosition, edge: .bottom))
            })
            PixelMovementSimulation.step(
                agents: &agents,
                deltaTime: 1.0 / 30.0,
                geometry: geometry,
                avoidanceRects: [],
                stoppedIDs: [],
                messageBubbleTangentRanges: ranges
            )

            for agent in agents {
                XCTAssertTrue(agent.trackPosition.isFinite)
                XCTAssertTrue(agent.velocity.isFinite)
                XCTAssertTrue(geometry.trackRange.contains(agent.trackPosition))
                XCTAssertLessThanOrEqual(abs(agent.velocity), PixelMovementSimulation.messageBubbleMaximumSpeed)
                if abs(agent.velocity) == PixelMovementSimulation.messageBubbleMaximumSpeed {
                    reachedMaximumSpeed = true
                }
                XCTAssertLessThanOrEqual(
                    abs(agent.trackPosition - (previousPositions[agent.id] ?? agent.trackPosition)),
                    PixelMovementSimulation.messageBubbleMaximumSpeed / 30 + 0.001
                )
                let direction = agent.velocity == 0 ? CGFloat.zero : (agent.velocity < 0 ? -1 : 1)
                if direction != 0,
                   let previous = lastDirections[agent.id],
                   previous != 0,
                   previous != direction {
                    directionChanges[agent.id, default: 0] += 1
                }
                if direction != 0 {
                    lastDirections[agent.id] = direction
                }
            }
        }

        XCTAssertTrue(directionChanges.values.allSatisfy { $0 <= 1 })
        XCTAssertTrue(reachedMaximumSpeed)
    }

    func testMessageBubbleResolutionRestoresWalkingLimitAndOriginalTargets() {
        let geometry = EdgeTrackGeometry(
            bounds: CGRect(x: 0, y: 0, width: 620, height: 240),
            edge: .bottom
        )
        var agents = [
            PixelMovementAgent(id: UUID(), trackPosition: 240, target: 560),
            PixelMovementAgent(id: UUID(), trackPosition: 300, target: 60)
        ]
        let originalTargets = agents.map(\.target)

        for _ in 0..<60 {
            PixelMovementSimulation.step(
                agents: &agents,
                deltaTime: 1.0 / 30.0,
                geometry: geometry,
                avoidanceRects: [],
                stoppedIDs: [],
                messageBubbleTangentRanges: messageBubbleRanges(agents: agents, halfWidth: 60)
            )
            if messageBubbleGap(agents: agents, halfWidth: 60)
                >= PixelMovementSimulation.messageBubbleClearance {
                break
            }
        }
        XCTAssertGreaterThanOrEqual(
            messageBubbleGap(agents: agents, halfWidth: 60),
            PixelMovementSimulation.messageBubbleClearance
        )
        XCTAssertEqual(agents.map(\.target), originalTargets)

        PixelMovementSimulation.step(
            agents: &agents,
            deltaTime: 1.0 / 30.0,
            geometry: geometry,
            avoidanceRects: [],
            stoppedIDs: [],
            messageBubbleTangentRanges: [:]
        )

        XCTAssertTrue(agents.allSatisfy { abs($0.velocity) <= PixelMovementSimulation.maximumSpeed })
        XCTAssertTrue(agents.allSatisfy { $0.messageBubbleSeparationOrder == nil })
        XCTAssertEqual(agents.map(\.target), originalTargets)
    }

    func testMessageBubbleDoesNotStopAnOnlineSender() {
        let movingID = UUID()
        let awayID = UUID()
        let members = [
            PixelWorldMember(
                id: movingID, nickname: "온라인", characterID: "pixel_hamster",
                presence: .online, isTyping: false, isCurrentUser: true
            ),
            PixelWorldMember(
                id: awayID, nickname: "자리 비움", characterID: "pixel_cat",
                presence: .away, isTyping: false, isCurrentUser: false
            )
        ]
        let stoppedIDs = PixelMovementPolicy.stoppedMemberIDs(in: members)
        XCTAssertFalse(stoppedIDs.contains(movingID))
        XCTAssertTrue(stoppedIDs.contains(awayID))

        var agents = [PixelMovementAgent(id: movingID, trackPosition: 40, target: 200)]

        PixelMovementSimulation.step(
            agents: &agents,
            deltaTime: 1,
            geometry: EdgeTrackGeometry(
                bounds: CGRect(x: 0, y: 0, width: 300, height: 240),
                edge: .bottom
            ),
            avoidanceRects: [],
            stoppedIDs: stoppedIDs
        )

        XCTAssertNotEqual(agents.first?.trackPosition, 40)
    }

    func testUUIDDiffAndCharacterSwapKeepTrackPosition() throws {
        let roomID = UUID()
        let memberID = UUID()
        let member = PixelWorldMember(
            id: memberID,
            nickname: "친구",
            characterID: "pixel_hamster",
            presence: .online,
            isTyping: false,
            isCurrentUser: true
        )
        let scene = PixelWorldScene(size: CGSize(width: 1_000, height: 240))
        scene.apply(roomID: roomID, members: [member], bubbles: [], edge: .bottom, installationSeed: 42)
        let initial = try XCTUnwrap(scene.agentStates.first?.trackPosition)

        let updated = PixelWorldMember(
            id: memberID,
            nickname: "친구",
            characterID: "pixel_cat",
            presence: .away,
            isTyping: false,
            isCurrentUser: true
        )
        scene.apply(roomID: roomID, members: [updated], bubbles: [], edge: .left, installationSeed: 42)

        XCTAssertEqual(scene.nodeIDs, [memberID])
        XCTAssertEqual(scene.agentStates.first?.trackPosition, initial)
        XCTAssertEqual(scene.renderedCharacterID(for: memberID), "pixel_cat")
    }

    func testStableSeedReproducesInitialRoomPlacement() {
        let roomID = UUID()
        let member = makeMember()
        let first = PixelWorldScene(size: CGSize(width: 800, height: 240))
        let second = PixelWorldScene(size: CGSize(width: 800, height: 240))

        first.apply(roomID: roomID, members: [member], bubbles: [], edge: .bottom, installationSeed: 12_345)
        second.apply(roomID: roomID, members: [member], bubbles: [], edge: .bottom, installationSeed: 12_345)

        XCTAssertEqual(first.agentStates.first?.trackPosition, second.agentStates.first?.trackPosition)
    }

    func testBubbleLayoutKeepsShortLongAndThreeLineMessagesInsideCanvas() {
        let messages = [
            "가",
            String(repeating: "긴메시지", count: 50),
            "첫 줄\n둘째 줄\n셋째 줄"
        ]
        for edge in OverlayEdge.allCases {
            let bounds = edge.isHorizontal
                ? CGRect(x: 0, y: 0, width: 720, height: 240)
                : CGRect(x: 0, y: 0, width: 240, height: 720)
            let geometry = EdgeTrackGeometry(bounds: bounds, edge: edge)
            for tangent in [geometry.trackRange.lowerBound, geometry.trackRange.upperBound] {
                for message in messages {
                    let layout = PixelBubbleLayout.make(
                        text: message,
                        isTyping: false,
                        tangentPosition: tangent,
                        tangentLength: geometry.tangentLength,
                        edge: edge,
                        maximumContentWidth: edge.isHorizontal
                            ? nil
                            : max(24, bounds.width - EdgeTrackGeometry.footInset - 56)
                    )
                    let worldFrame = geometry.worldFrame(for: layout.totalFrame, at: tangent)
                    XCTAssertTrue(
                        bounds.insetBy(dx: -0.5, dy: -0.5).contains(worldFrame),
                        "\(edge) \(tangent) \(message.count): \(worldFrame)"
                    )
                    XCTAssertLessThanOrEqual(layout.size.width, 220)
                    let tangentRange = layout.bodyTangentRange(at: tangent, edge: edge)
                    let worldBodyFrame = geometry.worldFrame(for: layout.bodyFrame, at: tangent)
                    let expectedRange = edge.isHorizontal
                        ? (worldBodyFrame.minX - bounds.minX)...(worldBodyFrame.maxX - bounds.minX)
                        : (worldBodyFrame.minY - bounds.minY)...(worldBodyFrame.maxY - bounds.minY)
                    XCTAssertEqual(tangentRange.lowerBound, expectedRange.lowerBound, accuracy: 0.001)
                    XCTAssertEqual(tangentRange.upperBound, expectedRange.upperBound, accuracy: 0.001)
                }
            }
        }
    }

    func testTwoMaximumLengthBubblesStackInsideCanvasOnAllEdges() {
        let senderID = UUID()
        let bubbles = [
            ActiveBubble(
                senderID: senderID,
                messageID: UUID(),
                body: String(repeating: "가", count: 200),
                expiresAt: Date(timeIntervalSince1970: 10),
                bubbleStyleID: "bubble_bunny_pink"
            ),
            ActiveBubble(
                senderID: senderID,
                messageID: UUID(),
                body: String(repeating: "나", count: 200),
                expiresAt: Date(timeIntervalSince1970: 11),
                bubbleStyleID: "bubble_starry_cat"
            )
        ]

        for edge in OverlayEdge.allCases {
            let bounds = edge.isHorizontal
                ? CGRect(x: 0, y: 0, width: 720, height: 360)
                : CGRect(x: 0, y: 0, width: 360, height: 720)
            let geometry = EdgeTrackGeometry(bounds: bounds, edge: edge)
            for tangent in [geometry.trackRange.lowerBound, geometry.trackRange.upperBound] {
                let entries = PixelBubbleStackLayout.make(
                    bubbles: bubbles,
                    tangentPosition: tangent,
                    tangentLength: geometry.tangentLength,
                    edge: edge,
                    inwardNormalLength: edge.isHorizontal
                        ? bounds.height - EdgeTrackGeometry.footInset
                        : bounds.width - EdgeTrackGeometry.footInset
                )

                XCTAssertEqual(entries.map(\.bubble.body), bubbles.map(\.body))
                XCTAssertEqual(entries.map(\.includesTail), [false, true])
                if edge.isHorizontal {
                    XCTAssertEqual(
                        entries[0].layout.bodyFrame.minY - entries[1].layout.bodyFrame.maxY,
                        PixelBubbleStackLayout.bodySpacing,
                        accuracy: 0.001
                    )
                } else {
                    let worldVisualFrames = entries.map {
                        geometry.worldFrame(for: $0.layout.visualFrame, at: tangent)
                    }
                    XCTAssertEqual(
                        worldVisualFrames[0].minY - worldVisualFrames[1].maxY,
                        PixelBubbleStackLayout.bodySpacing,
                        accuracy: 0.001
                    )
                    let worldBodyFrames = entries.map {
                        geometry.worldFrame(for: $0.layout.bodyFrame, at: tangent)
                    }
                    if edge == .left {
                        XCTAssertEqual(
                            worldBodyFrames[0].minX,
                            worldBodyFrames[1].minX,
                            accuracy: 0.001
                        )
                    } else {
                        XCTAssertEqual(
                            worldBodyFrames[0].maxX,
                            worldBodyFrames[1].maxX,
                            accuracy: 0.001
                        )
                    }
                    XCTAssertEqual(
                        entries[1].layout.tailTipInPresentation,
                        CGPoint(x: 0, y: 44)
                    )
                }
                for entry in entries {
                    let worldFrame = geometry.worldFrame(for: entry.layout.totalFrame, at: tangent)
                    XCTAssertTrue(
                        bounds.insetBy(dx: -0.5, dy: -0.5).contains(worldFrame),
                        "\(edge) \(tangent): \(worldFrame)"
                    )
                }
            }
        }
    }

    func testSceneRendersSideBubblesVerticallyWithNewestTailAndUnionCollisionRange() throws {
        let roomID = UUID()
        let member = makeMember()
        let bubbles = [
            ActiveBubble(
                senderID: member.id,
                messageID: UUID(),
                body: "이전 " + String(repeating: "긴메시지", count: 20),
                expiresAt: Date(timeIntervalSince1970: 10)
            ),
            ActiveBubble(
                senderID: member.id,
                messageID: UUID(),
                body: "최신",
                expiresAt: Date(timeIntervalSince1970: 11)
            )
        ]

        for edge in [OverlayEdge.left, .right] {
            let scene = PixelWorldScene(size: CGSize(width: 360, height: 720))
            scene.apply(
                roomID: roomID,
                members: [member],
                bubbles: bubbles,
                edge: edge,
                installationSeed: 9
            )

            XCTAssertEqual(scene.renderedBubbleBodies(for: member.id), bubbles.map(\.body))
            XCTAssertEqual(scene.renderedBubbleTailFlags(for: member.id), [false, true])
            XCTAssertTrue(scene.renderedBubbleWorldRotations(for: member.id).allSatisfy {
                abs($0) < 0.001
            })
            let tangent = try XCTUnwrap(scene.agentStates.first?.trackPosition)
            let worldBodyFrames = scene.renderedBubbleBodyFrames(for: member.id).map {
                scene.trackGeometry.worldFrame(for: $0, at: tangent)
            }
            XCTAssertEqual(worldBodyFrames.count, 2)
            XCTAssertEqual(
                worldBodyFrames[0].minY - worldBodyFrames[1].maxY,
                PixelBubbleStackLayout.bodySpacing,
                accuracy: 0.001,
                "\(edge)"
            )
            XCTAssertTrue(worldBodyFrames.allSatisfy {
                scene.frame.insetBy(dx: -0.5, dy: -0.5).contains($0)
            })
            let collisionRange = try XCTUnwrap(scene.messageBubbleTangentRanges[member.id])
            let expectedLowerBound = try XCTUnwrap(worldBodyFrames.map(\.minY).min())
            let expectedUpperBound = try XCTUnwrap(worldBodyFrames.map(\.maxY).max())
            XCTAssertEqual(
                collisionRange.lowerBound,
                expectedLowerBound,
                accuracy: 0.001,
                "\(edge)"
            )
            XCTAssertEqual(
                collisionRange.upperBound,
                expectedUpperBound,
                accuracy: 0.001,
                "\(edge)"
            )
        }
    }

    func testSceneRendersOlderBubbleAboveNewestWithOnlyNewestTailAndUnionCollisionRange() {
        let roomID = UUID()
        let member = makeMember()
        let bubbles = [
            ActiveBubble(
                senderID: member.id,
                messageID: UUID(),
                body: "이전 " + String(repeating: "긴메시지", count: 20),
                expiresAt: Date(timeIntervalSince1970: 10)
            ),
            ActiveBubble(
                senderID: member.id,
                messageID: UUID(),
                body: "최신",
                expiresAt: Date(timeIntervalSince1970: 11)
            )
        ]
        let scene = PixelWorldScene(size: CGSize(width: 720, height: 360))
        scene.apply(
            roomID: roomID,
            members: [member],
            bubbles: bubbles,
            edge: .bottom,
            installationSeed: 9
        )

        XCTAssertEqual(scene.renderedBubbleBodies(for: member.id), bubbles.map(\.body))
        XCTAssertEqual(scene.renderedBubbleTailFlags(for: member.id), [false, true])
        let frames = scene.renderedBubbleBodyFrames(for: member.id)
        XCTAssertEqual(frames.count, 2)
        XCTAssertEqual(
            frames[0].minY - frames[1].maxY,
            PixelBubbleStackLayout.bodySpacing,
            accuracy: 0.001
        )

        let range = scene.messageBubbleTangentRanges[member.id]
        let geometry = scene.trackGeometry
        let tangent = try! XCTUnwrap(scene.agentStates.first?.trackPosition)
        let expectedEntries = PixelBubbleStackLayout.make(
            bubbles: bubbles,
            tangentPosition: tangent,
            tangentLength: geometry.tangentLength,
            edge: .bottom
        )
        XCTAssertEqual(
            range,
            PixelBubbleStackLayout.bodyTangentRange(
                for: expectedEntries,
                at: tangent,
                edge: .bottom
            )
        )
    }

    func testSideEdgeBubbleLayoutUsesUprightBodyFrameAndTailTargetsCharacter() {
        let renderBounds = CGRect(x: 0, y: 0, width: 360, height: 720)

        for edge in [OverlayEdge.left, .right] {
            let activityBounds = CGRect(
                x: edge == .left ? 0 : 120,
                y: 0,
                width: 240,
                height: 720
            )
            let geometry = EdgeTrackGeometry(bounds: activityBounds, edge: edge)

            for tangent in [geometry.trackRange.lowerBound, geometry.trackRange.upperBound] {
                let layout = PixelBubbleLayout.make(
                    text: String(repeating: "upright 긴 메시지 ", count: 16),
                    isTyping: false,
                    tangentPosition: tangent,
                    tangentLength: geometry.tangentLength,
                    edge: edge,
                    leadingDecorationOverflow: PixelBubbleStyle.decorationLeadingOverflow,
                    maximumContentWidth: 220
                )
                let worldBodyFrame = geometry.worldFrame(for: layout.bodyFrame, at: tangent)
                let worldTotalFrame = geometry.worldFrame(for: layout.totalFrame, at: tangent)

                XCTAssertEqual(edge.presentationRotation + layout.nodeRotation, 0, accuracy: 0.001)
                XCTAssertEqual(worldBodyFrame.width, layout.size.width, accuracy: 0.001)
                XCTAssertEqual(worldBodyFrame.height, layout.size.height, accuracy: 0.001)
                XCTAssertEqual(layout.tailTipInPresentation.x, 0, accuracy: 0.001)
                XCTAssertEqual(layout.tailTipInPresentation.y, 44, accuracy: 0.001)
                XCTAssertEqual(layout.tailAttachment, edge == .left ? .left : .right)
                XCTAssertTrue(
                    renderBounds.insetBy(dx: -0.5, dy: -0.5).contains(worldTotalFrame),
                    "\(edge) \(tangent): \(worldTotalFrame)"
                )

                let tangentRange = layout.bodyTangentRange(at: tangent, edge: edge)
                XCTAssertEqual(tangentRange.lowerBound, worldBodyFrame.minY, accuracy: 0.001)
                XCTAssertEqual(tangentRange.upperBound, worldBodyFrame.maxY, accuracy: 0.001)
            }
        }
    }

    func testTopEdgeBubbleKeepsTailOnCharacterSideOfUprightBody() {
        let layout = PixelBubbleLayout.make(
            text: "상단에서도 바로 읽어요",
            isTyping: false,
            tangentPosition: 200,
            tangentLength: 400,
            edge: .top
        )

        XCTAssertEqual(OverlayEdge.top.presentationRotation + layout.nodeRotation, 0, accuracy: 0.001)
        XCTAssertEqual(layout.labelRotation, 0, accuracy: 0.001)
        XCTAssertEqual(layout.tailAttachment, .top)
        XCTAssertGreaterThan(layout.tailTip.y, layout.size.height / 2)
        XCTAssertEqual(layout.tailTipInPresentation.y, 44, accuracy: 0.001)
    }

    func testDecoratedSideTypingBubbleUsesItsUprightVisualFrameForBoundaryClamp() throws {
        let member = PixelWorldMember(
            id: UUID(),
            nickname: "typing",
            characterID: "pixel_hamster",
            presence: .typing,
            isTyping: true,
            isCurrentUser: false,
            equippedBubbleStyleID: "bubble_bunny_pink"
        )
        let sceneBounds = CGRect(x: 0, y: 0, width: 100, height: 720)

        for edge in [OverlayEdge.left, .right] {
            let scene = PixelWorldScene(size: sceneBounds.size)
            scene.apply(
                roomID: UUID(),
                members: [member],
                bubbles: [],
                edge: edge,
                installationSeed: 1
            )
            let tangent = try XCTUnwrap(scene.agentStates.first?.trackPosition)
            let localFrame = try XCTUnwrap(scene.renderedBubbleTotalFrames(for: member.id).first)
            let worldFrame = scene.trackGeometry.worldFrame(for: localFrame, at: tangent)

            XCTAssertTrue(
                sceneBounds.insetBy(dx: -0.5, dy: -0.5).contains(worldFrame),
                "\(edge): \(worldFrame)"
            )
        }
    }

    func testSceneKeepsTwoBubblesForAllTwelveMembers() {
        let roomID = UUID()
        let members = (0..<12).map { index in
            PixelWorldMember(
                id: UUID(),
                nickname: "친구\(index)",
                characterID: "pixel_hamster",
                presence: .online,
                isTyping: true,
                isCurrentUser: index == 0
            )
        }
        let bubbles = members.flatMap { member in
            [
                ActiveBubble(
                    senderID: member.id,
                    messageID: UUID(),
                    body: "이전",
                    expiresAt: Date(timeIntervalSince1970: 10)
                ),
                ActiveBubble(
                    senderID: member.id,
                    messageID: UUID(),
                    body: "최신",
                    expiresAt: Date(timeIntervalSince1970: 11)
                )
            ]
        }
        let scene = PixelWorldScene(size: CGSize(width: 1_200, height: 360))

        scene.apply(
            roomID: roomID,
            members: members,
            bubbles: bubbles,
            edge: .bottom,
            installationSeed: 10
        )

        XCTAssertEqual(scene.messageBubbleTangentRanges.count, 12)
        for member in members {
            XCTAssertEqual(scene.renderedBubbleBodies(for: member.id), ["이전", "최신"])
            XCTAssertEqual(scene.renderedBubbleTailFlags(for: member.id), [false, true])
            XCTAssertFalse(scene.renderedBubbleIsTyping(for: member.id))
        }
    }

    func testBubbleUsesExplicitDarkInkOnLightBackground() throws {
        let text = PixelBubbleStyle.textColor.usingColorSpace(.sRGB) ?? PixelBubbleStyle.textColor
        let background = PixelBubbleStyle.backgroundColor.usingColorSpace(.sRGB)
            ?? PixelBubbleStyle.backgroundColor
        XCTAssertLessThan(text.redComponent, 0.2)
        XCTAssertLessThan(text.greenComponent, 0.2)
        XCTAssertLessThan(text.blueComponent, 0.2)
        XCTAssertGreaterThan(background.redComponent, 0.9)
        XCTAssertGreaterThan(background.greenComponent, 0.9)
        XCTAssertGreaterThan(background.blueComponent, 0.9)

        let layout = PixelBubbleLayout.make(
            text: "ㅎㅎㅎㅎㅎㅎㅎㅎㅎㅎ",
            isTyping: false,
            tangentPosition: 200,
            tangentLength: 720,
            edge: .bottom
        )
        let node = PixelBubbleNode(body: "ㅎㅎㅎㅎㅎㅎㅎㅎㅎㅎ", isTyping: false, layout: layout)
        let label = try XCTUnwrap(node.children.compactMap { $0 as? SKLabelNode }.first)
        XCTAssertEqual(label.fontColor, PixelBubbleStyle.textColor)
    }

    func testApprovedBubbleThemesApplyIndependentReadableTextColorsToMessagesAndTyping() throws {
        let cases: [(String, NSColor, NSColor)] = [
            (
                "bubble_bunny_pink",
                NSColor(srgbRed: 0xF7 / 255, green: 0xA9 / 255, blue: 0xB8 / 255, alpha: 0.96),
                NSColor(srgbRed: 0x1C / 255, green: 0x1F / 255, blue: 0x29 / 255, alpha: 1)
            ),
            (
                "bubble_butter_chick",
                NSColor(srgbRed: 0xFF / 255, green: 0xE3 / 255, blue: 0x8A / 255, alpha: 0.96),
                NSColor(srgbRed: 0x1C / 255, green: 0x1F / 255, blue: 0x29 / 255, alpha: 1)
            ),
            (
                "bubble_starry_cat",
                NSColor(srgbRed: 0x40 / 255, green: 0x3A / 255, blue: 0x78 / 255, alpha: 0.96),
                NSColor(srgbRed: 0xFF / 255, green: 0xF7 / 255, blue: 0xE8 / 255, alpha: 1)
            ),
        ]
        let layout = PixelBubbleLayout.make(
            text: "오늘도 같이 있자!",
            isTyping: false,
            tangentPosition: 200,
            tangentLength: 720,
            edge: .bottom
        )

        for (styleID, background, text) in cases {
            let message = PixelBubbleNode(
                body: "오늘도 같이 있자!",
                isTyping: false,
                layout: layout,
                bubbleStyleID: styleID
            )
            let messageLabel = try XCTUnwrap(message.children.compactMap { $0 as? SKLabelNode }.first)
            XCTAssertEqual(message.theme.id, styleID)
            XCTAssertEqual(message.theme.backgroundColor, background)
            XCTAssertEqual(messageLabel.fontColor, text)
            XCTAssertEqual(message.theme.decorationAssetName, styleID)

            let typing = TypingIndicatorNode(layout: layout, bubbleStyleID: styleID)
            let typingLabel = try XCTUnwrap(typing.children.compactMap { $0 as? SKLabelNode }.first)
            XCTAssertEqual(typing.theme.id, styleID)
            XCTAssertEqual(typingLabel.fontColor, text)
        }

        XCTAssertNil(PixelBubbleTheme.resolve("future_unknown_style").id)
        XCTAssertEqual(
            PixelBubbleTheme.resolve("future_unknown_style").backgroundColor,
            PixelBubbleStyle.backgroundColor
        )
    }

    func testTypingDotsMessagePriorityAndTypingReturn() {
        XCTAssertEqual(TypingIndicatorNode.sequenceFrames, [".", "..", "..."])
        XCTAssertEqual(TypingIndicatorNode.frameInterval, 0.35, accuracy: 0.001)

        let roomID = UUID()
        let member = PixelWorldMember(
            id: UUID(), nickname: "친구", characterID: "pixel_rabbit",
            presence: .online, isTyping: true, isCurrentUser: false
        )
        let scene = PixelWorldScene(size: CGSize(width: 720, height: 240))
        scene.apply(roomID: roomID, members: [member], bubbles: [], edge: .bottom, installationSeed: 1)
        XCTAssertTrue(scene.renderedBubbleIsTyping(for: member.id))
        XCTAssertEqual(scene.renderedBubbleBody(for: member.id), ".")

        let message = ActiveBubble(
            senderID: member.id,
            messageID: UUID(),
            body: "도착",
            expiresAt: .now.addingTimeInterval(10)
        )
        let newerMessage = ActiveBubble(
            senderID: member.id,
            messageID: UUID(),
            body: "또 도착",
            expiresAt: .now.addingTimeInterval(11)
        )
        scene.apply(
            roomID: roomID,
            members: [member],
            bubbles: [message, newerMessage],
            edge: .bottom,
            installationSeed: 1
        )
        XCTAssertFalse(scene.renderedBubbleIsTyping(for: member.id))
        XCTAssertEqual(scene.renderedBubbleBodies(for: member.id), ["도착", "또 도착"])
        XCTAssertEqual(scene.renderedBubbleBody(for: member.id), "또 도착")

        scene.apply(
            roomID: roomID,
            members: [member],
            bubbles: [newerMessage],
            edge: .bottom,
            installationSeed: 1
        )
        XCTAssertFalse(scene.renderedBubbleIsTyping(for: member.id))
        XCTAssertEqual(scene.renderedBubbleBodies(for: member.id), ["또 도착"])

        scene.apply(roomID: roomID, members: [member], bubbles: [], edge: .bottom, installationSeed: 1)
        XCTAssertTrue(scene.renderedBubbleIsTyping(for: member.id))
    }

    func testCharacterPulsePlaysOncePerBroadcastEvent() {
        let roomID = UUID()
        let member = makeMember(isCurrentUser: true)
        let scene = PixelWorldScene(size: CGSize(width: 720, height: 240))
        let first = CharacterPulseEvent(id: UUID(), roomID: roomID, userID: member.id)

        scene.apply(
            roomID: roomID, members: [member], bubbles: [], edge: .bottom,
            installationSeed: 1, characterPulse: first
        )
        XCTAssertEqual(scene.renderedPulseCount(for: member.id), 1)

        scene.apply(
            roomID: roomID, members: [member], bubbles: [], edge: .bottom,
            installationSeed: 1, characterPulse: first
        )
        XCTAssertEqual(scene.renderedPulseCount(for: member.id), 1)

        scene.apply(
            roomID: roomID, members: [member], bubbles: [], edge: .bottom,
            installationSeed: 1,
            characterPulse: CharacterPulseEvent(id: UUID(), roomID: roomID, userID: member.id)
        )
        XCTAssertEqual(scene.renderedPulseCount(for: member.id), 2)
        XCTAssertEqual(PixelCharacterPulseStyle.peakScale, 7)
        XCTAssertEqual(PixelCharacterPulseStyle.growDuration, 0.20, accuracy: 0.001)
        XCTAssertEqual(PixelCharacterPulseStyle.settleDuration, 0.60, accuracy: 0.001)
        XCTAssertEqual(PixelCharacterPulseStyle.totalDuration, 0.80, accuracy: 0.001)
    }

    func testCharacterThrowDeduplicatesEventAndCompletesWithHit() {
        let roomID = UUID()
        let actor = makeMember(isCurrentUser: true)
        let target = PixelWorldMember(
            id: UUID(), nickname: "친구", characterID: "pixel_cat",
            presence: .online, isTyping: false, isCurrentUser: false
        )
        let event = CharacterThrowEvent(
            id: UUID(), roomID: roomID, actorUserID: actor.id,
            targetUserID: target.id, sourceCharacterID: PixelCharacterCatalog.pixelHamsterID
        )
        let scene = PixelWorldScene(size: CGSize(width: 720, height: 360))

        scene.apply(
            roomID: roomID, members: [actor, target], bubbles: [], edge: .bottom,
            installationSeed: 1, characterThrow: event
        )
        scene.apply(
            roomID: roomID, members: [actor, target], bubbles: [], edge: .bottom,
            installationSeed: 1, characterThrow: event
        )
        XCTAssertEqual(scene.renderedThrowCount(for: actor.id), 1)
        XCTAssertEqual(scene.activeProjectileCount, 1)

        scene.update(ProcessInfo.processInfo.systemUptime + 2)
        XCTAssertEqual(scene.activeProjectileCount, 0)
        XCTAssertEqual(scene.renderedHitCount(for: target.id), 1)
    }

    func testCharacterThrowCompletesWithHitForOfflineTarget() {
        let roomID = UUID()
        let actor = makeMember(isCurrentUser: true)
        let target = PixelWorldMember(
            id: UUID(), nickname: "오프라인", characterID: "pixel_cat",
            presence: .offline, isTyping: false, isCurrentUser: false
        )
        let event = CharacterThrowEvent(
            id: UUID(), roomID: roomID, actorUserID: actor.id,
            targetUserID: target.id, sourceCharacterID: PixelCharacterCatalog.pixelHamsterID
        )
        let scene = PixelWorldScene(size: CGSize(width: 720, height: 360))

        scene.apply(
            roomID: roomID, members: [actor, target], bubbles: [], edge: .bottom,
            installationSeed: 1, characterThrow: event
        )
        scene.update(ProcessInfo.processInfo.systemUptime + 2)

        XCTAssertEqual(scene.activeProjectileCount, 0)
        XCTAssertEqual(scene.renderedHitCount(for: target.id), 1)
    }

    func testCharacterThrowCatalogMappingAndTimingContract() {
        XCTAssertEqual(PixelCharacterThrowCatalog.objectID(for: "pixel_cat"), "patch_soft_ball")
        for characterID in ["pixel_guinea_pig", "pixel_monkey", "pixel_chinchilla", "pixel_starlight_upalupa", "unknown"] {
            XCTAssertEqual(PixelCharacterThrowCatalog.objectID(for: characterID), "patch_soft_ball")
            XCTAssertEqual(PixelCharacterThrowCatalog.interactionDescription(for: characterID),
                           L10n.text("pixel_world.interaction.throw"))
        }
        XCTAssertEqual(
            PixelCharacterThrowCatalog.resolvedObjectID(
                for: "pixel_cat",
                equippedObjectID: "throwable_toy_cannon"
            ),
            "throwable_toy_cannon"
        )
        XCTAssertEqual(
            PixelCharacterThrowCatalog.resolvedObjectID(
                for: "pixel_cat",
                equippedObjectID: "future_unknown_throwable"
            ),
            "patch_soft_ball"
        )
        XCTAssertEqual(PixelCharacterThrowStyle.arcHeight(for: 10), 24)
        XCTAssertEqual(PixelCharacterThrowStyle.arcHeight(for: 1_000), 96)
        XCTAssertEqual(PixelCharacterThrowStyle.flightDuration(for: 0), 0.35, accuracy: 0.001)
        XCTAssertEqual(PixelCharacterThrowStyle.flightDuration(for: 2_000), 0.95, accuracy: 0.001)
        XCTAssertEqual(PixelCharacterThrowStyle.impactPointSize, 48)
        XCTAssertEqual(PixelCharacterThrowStyle.impactTorsoOffset, 10)
        XCTAssertEqual(PixelCharacterThrowStyle.cannonEmitterPointSize, 48)
        XCTAssertGreaterThan(PixelCharacterThrowStyle.cannonEmitterZPosition, 0)
        XCTAssertEqual(PixelCharacterThrowStyle.cannonEmitterTangentOffset, 6)
        XCTAssertEqual(PixelCharacterThrowStyle.maximumActiveProjectiles, 32)
    }

    func testAllEdgesKeepNicknameAndCompleteMessageAndTypingBubblesUpright() throws {
        let member = makeMember()
        let bubble = ActiveBubble(
            senderID: member.id,
            messageID: UUID(),
            body: "똑바로 보여요",
            expiresAt: .distantFuture
        )
        let scene = PixelWorldScene(size: CGSize(width: 720, height: 360))
        let roomID = UUID()

        for edge in OverlayEdge.allCases {
            scene.apply(
                roomID: roomID,
                members: [member],
                bubbles: [bubble],
                edge: edge,
                installationSeed: 1
            )
            XCTAssertEqual(
                try XCTUnwrap(scene.renderedNicknameWorldRotation(for: member.id)),
                0,
                accuracy: 0.001,
                "\(edge)"
            )
            XCTAssertEqual(
                try XCTUnwrap(scene.renderedBubbleTextWorldRotations(for: member.id).first),
                0,
                accuracy: 0.001,
                "\(edge)"
            )
            XCTAssertEqual(
                try XCTUnwrap(scene.renderedBubbleWorldRotations(for: member.id).first),
                0,
                accuracy: 0.001,
                "\(edge)"
            )
            XCTAssertEqual(scene.renderedBubbleBody(for: member.id), bubble.body)
        }

        let typingMember = PixelWorldMember(
            id: member.id,
            nickname: member.nickname,
            characterID: member.characterID,
            presence: .typing,
            isTyping: true,
            isCurrentUser: false
        )
        for edge in OverlayEdge.allCases {
            scene.apply(
                roomID: roomID,
                members: [typingMember],
                bubbles: [],
                edge: edge,
                installationSeed: 1
            )
            XCTAssertEqual(
                try XCTUnwrap(scene.renderedBubbleTextWorldRotations(for: member.id).first),
                0,
                accuracy: 0.001,
                "\(edge)"
            )
            XCTAssertEqual(
                try XCTUnwrap(scene.renderedBubbleWorldRotations(for: member.id).first),
                0,
                accuracy: 0.001,
                "\(edge)"
            )
        }
    }

    func testCharacterFramesCallbackReportsEveryMemberAs52Points() {
        let members = [makeMember(isCurrentUser: true), makeMember(isCurrentUser: false)]
        var reported: [UUID: CGRect] = [:]
        let scene = PixelWorldScene(size: CGSize(width: 720, height: 240))

        scene.apply(
            roomID: UUID(), members: members, bubbles: [], edge: .bottom,
            installationSeed: 1, onCharacterFramesChanged: { reported = $0 }
        )

        XCTAssertEqual(Set(reported.keys), Set(members.map(\.id)))
        XCTAssertTrue(reported.values.allSatisfy { $0.size == CGSize(width: 52, height: 52) })
    }

    func testStarlightSparklesAreCatalogDrivenAndStopOutsideOnlinePresence() {
        let roomID = UUID()
        let memberID = UUID()
        let scene = PixelWorldScene(size: CGSize(width: 720, height: 240))

        func apply(_ presence: PresenceState, pulse: CharacterPulseEvent? = nil) {
            scene.apply(
                roomID: roomID,
                members: [PixelWorldMember(
                    id: memberID,
                    nickname: "친구",
                    characterID: PixelCharacterCatalog.pixelStarlightUpalupaID,
                    presence: presence,
                    isTyping: presence == .typing,
                    isCurrentUser: false
                )],
                bubbles: [],
                edge: .bottom,
                installationSeed: 2,
                characterPulse: pulse
            )
        }

        apply(.online)
        XCTAssertTrue(scene.hasRenderedAmbientSparkles(for: memberID))
        apply(.typing)
        XCTAssertTrue(scene.hasRenderedAmbientSparkles(for: memberID))
        for presence in [PresenceState.away, .offline, .reconnecting] {
            apply(presence)
            XCTAssertFalse(scene.hasRenderedAmbientSparkles(for: memberID), "\(presence)")
        }

        apply(.online, pulse: CharacterPulseEvent(id: UUID(), roomID: roomID, userID: memberID))
        XCTAssertEqual(scene.renderedPulseSparkleCount(for: memberID), 42)
    }

    func testStarlightFacingFollowsTrackVelocityOnEveryEdgeAndPersistsAtRest() {
        for edge in OverlayEdge.allCases {
            let expectedPositive: CGFloat = switch edge {
            case .bottom, .right: 1
            case .top, .left: -1
            }
            let positive = PixelCharacterFacingPolicy.scale(
                mirrorsToMovementDirection: true,
                velocity: 12,
                edge: edge,
                previousScale: 1
            )
            XCTAssertEqual(positive, expectedPositive, "\(edge)")
            XCTAssertEqual(PixelCharacterFacingPolicy.scale(
                mirrorsToMovementDirection: true,
                velocity: -12,
                edge: edge,
                previousScale: positive
            ), -positive, "\(edge)")
            XCTAssertEqual(PixelCharacterFacingPolicy.scale(
                mirrorsToMovementDirection: true,
                velocity: 0,
                edge: edge,
                previousScale: -positive
            ), -positive, "\(edge)")
        }

        XCTAssertEqual(PixelCharacterFacingPolicy.scale(
            mirrorsToMovementDirection: false,
            velocity: -12,
            edge: .bottom,
            previousScale: 1
        ), 1)
        XCTAssertEqual(PixelCharacterFacingPolicy.scale(
            mirrorsToMovementDirection: true,
            velocity: PixelCharacterFacingPolicy.movementThreshold,
            edge: .bottom,
            previousScale: -1
        ), -1)
    }

    func testAmbientSparkleVisibilityDependsOnlyOnOnlinePresence() {
        XCTAssertTrue(PixelSparkleVisibilityPolicy.showsAmbient(for: .online))
        XCTAssertTrue(PixelSparkleVisibilityPolicy.showsAmbient(for: .typing))
        XCTAssertFalse(PixelSparkleVisibilityPolicy.showsAmbient(for: .away))
        XCTAssertFalse(PixelSparkleVisibilityPolicy.showsAmbient(for: .offline))
        XCTAssertFalse(PixelSparkleVisibilityPolicy.showsAmbient(for: .reconnecting))
    }

    func testTwelveStarlightCharactersKeepIndependentAmbientAndPulseEffects() {
        let roomID = UUID()
        let members = (0..<12).map { index in
            PixelWorldMember(
                id: UUID(),
                nickname: "별\(index)",
                characterID: PixelCharacterCatalog.pixelStarlightUpalupaID,
                presence: .online,
                isTyping: false,
                isCurrentUser: index == 0
            )
        }
        let scene = PixelWorldScene(size: CGSize(width: 1_200, height: 360))
        scene.apply(
            roomID: roomID,
            members: members,
            bubbles: [],
            edge: .bottom,
            installationSeed: 9
        )
        XCTAssertTrue(members.allSatisfy {
            scene.hasRenderedAmbientSparkles(for: $0.id)
        })

        for member in members {
            scene.apply(
                roomID: roomID,
                members: members,
                bubbles: [],
                edge: .bottom,
                installationSeed: 9,
                characterPulse: CharacterPulseEvent(
                    id: UUID(),
                    roomID: roomID,
                    userID: member.id
                )
            )
        }
        XCTAssertEqual(
            members.reduce(0) { $0 + scene.renderedPulseSparkleCount(for: $1.id) },
            12 * PixelSparkleEffect.starlight.pulseCount
        )
    }

    func testAwayOfflineAndReconnectHaveDistinctVisualStates() throws {
        let scene = PixelWorldScene(size: CGSize(width: 720, height: 240))
        let roomID = UUID()
        let memberID = UUID()

        func apply(_ presence: PresenceState) -> PixelCharacterVisualState? {
            scene.apply(
                roomID: roomID,
                members: [PixelWorldMember(
                    id: memberID, nickname: "친구", characterID: "pixel_penguin",
                    presence: presence, isTyping: false, isCurrentUser: false
                )],
                bubbles: [], edge: .bottom, installationSeed: 2
            )
            return scene.renderedVisualState(for: memberID)
        }

        let away = try XCTUnwrap(apply(.away))
        XCTAssertEqual(away.motion, .doze)
        XCTAssertEqual(away.alpha, 1)
        XCTAssertEqual(away.colorBlendFactor, 0)
        XCTAssertTrue(away.showsDozeLabel)
        XCTAssertEqual(PixelDozeLabelStyle.text, "Zzz")
        XCTAssertEqual(PixelDozeLabelStyle.fontSize, 14)
        XCTAssertEqual(PixelDozeLabelStyle.outlineWidth, 2)
        XCTAssertEqual(PixelDozeLabelStyle.restingAlpha, 0.55, accuracy: 0.001)
        XCTAssertEqual(PixelDozeLabelStyle.floatingDistance, 3)

        let offline = try XCTUnwrap(apply(.offline))
        XCTAssertEqual(offline.motion, .offline)
        XCTAssertEqual(offline.alpha, 0.75, accuracy: 0.001)
        XCTAssertGreaterThan(offline.colorBlendFactor, 0.5)
        XCTAssertFalse(offline.showsDozeLabel)

        let reconnecting = try XCTUnwrap(apply(.reconnecting))
        XCTAssertEqual(reconnecting.motion, .stopped)
        XCTAssertFalse(reconnecting.showsDozeLabel)
    }

    func testDozeLabelStaysStaticAndStopsAnimatingWhenAwayEnds() throws {
        let scene = PixelWorldScene(size: CGSize(width: 720, height: 240))
        let roomID = UUID()
        let memberID = UUID()
        func apply(_ presence: PresenceState) {
            scene.apply(
                roomID: roomID,
                members: [PixelWorldMember(
                    id: memberID, nickname: "친구", characterID: "pixel_cat",
                    presence: presence, isTyping: false, isCurrentUser: false
                )],
                bubbles: [], edge: .bottom, installationSeed: 7
            )
        }

        apply(.away)
        XCTAssertEqual(scene.renderedDozeText(for: memberID), "Zzz")
        XCTAssertTrue(scene.hasRenderedDozeActions(for: memberID))
        apply(.online)
        XCTAssertEqual(scene.renderedDozeText(for: memberID), "Zzz")
        XCTAssertFalse(scene.hasRenderedDozeActions(for: memberID))
    }

    func testCurrentUserFrameCallbackUsesOnlyThe52PointHotspot() throws {
        let member = makeMember(isCurrentUser: true)
        var reported: CGRect?
        let scene = PixelWorldScene(size: CGSize(width: 720, height: 240))
        scene.apply(
            roomID: UUID(), members: [member], bubbles: [], edge: .bottom,
            installationSeed: 1, onCurrentUserFrameChanged: { reported = $0 }
        )
        XCTAssertEqual(try XCTUnwrap(reported).size, CGSize(width: 52, height: 52))

        scene.apply(
            roomID: nil, members: [], bubbles: [], edge: .bottom,
            installationSeed: 1, onCurrentUserFrameChanged: { reported = $0 }
        )
        XCTAssertNil(reported)
    }

    func testExpandedRenderSceneKeepsTrackAndHotspotInActivityFrameForEveryEdge() throws {
        let member = makeMember(isCurrentUser: true)
        let cases: [(OverlayEdge, CGSize, CGRect)] = [
            (.bottom, CGSize(width: 1_008, height: 360), CGRect(x: 144, y: 0, width: 720, height: 240)),
            (.top, CGSize(width: 1_008, height: 360), CGRect(x: 144, y: 120, width: 720, height: 240)),
            (.left, CGSize(width: 360, height: 1_008), CGRect(x: 0, y: 144, width: 240, height: 720)),
            (.right, CGSize(width: 360, height: 1_008), CGRect(x: 120, y: 144, width: 240, height: 720))
        ]

        for (edge, renderSize, activityFrame) in cases {
            var reported: CGRect?
            let scene = PixelWorldScene(size: renderSize)
            scene.apply(
                roomID: UUID(),
                members: [member],
                bubbles: [],
                edge: edge,
                activityFrame: activityFrame,
                installationSeed: 7,
                onCurrentUserFrameChanged: { reported = $0 }
            )

            XCTAssertEqual(scene.trackGeometry.bounds, activityFrame, "\(edge)")
            let hotspot = try XCTUnwrap(reported)
            XCTAssertEqual(hotspot.size, CGSize(width: 52, height: 52))
            let center = CGPoint(x: hotspot.midX, y: hotspot.midY)
            if edge.isHorizontal {
                XCTAssertTrue(activityFrame.minX...activityFrame.maxX ~= center.x, "\(edge)")
            } else {
                XCTAssertTrue(activityFrame.minY...activityFrame.maxY ~= center.y, "\(edge)")
            }
            let foot = scene.trackGeometry.footPoint(for: scene.agentStates[0].trackPosition)
            switch edge {
            case .bottom:
                XCTAssertEqual(foot.y, activityFrame.minY, accuracy: 0.001)
            case .top:
                XCTAssertEqual(foot.y, activityFrame.maxY, accuracy: 0.001)
            case .left:
                XCTAssertEqual(foot.x, activityFrame.minX, accuracy: 0.001)
            case .right:
                XCTAssertEqual(foot.x, activityFrame.maxX, accuracy: 0.001)
            }
        }
    }

    private func makeMember(isCurrentUser: Bool = false) -> PixelWorldMember {
        PixelWorldMember(
            id: UUID(), nickname: "친구", characterID: "pixel_hamster",
            presence: .online, isTyping: false, isCurrentUser: isCurrentUser
        )
    }

    private func messageBubbleRanges(
        agents: [PixelMovementAgent],
        halfWidth: CGFloat
    ) -> [UUID: ClosedRange<CGFloat>] {
        Dictionary(uniqueKeysWithValues: agents.map { agent in
            (agent.id, (agent.trackPosition - halfWidth)...(agent.trackPosition + halfWidth))
        })
    }

    private func messageBubbleGap(agents: [PixelMovementAgent], halfWidth: CGFloat) -> CGFloat {
        let ordered = agents.sorted { $0.trackPosition < $1.trackPosition }
        guard ordered.count >= 2 else { return .infinity }
        return (ordered[1].trackPosition - halfWidth) - (ordered[0].trackPosition + halfWidth)
    }
}
