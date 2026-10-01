import AppKit
import XCTest
@testable import SIDEY

@MainActor
final class CharacterDragTests: XCTestCase {
    func testPullConstrainsRopeAndBodyLagsThenSwingsUnderPin() {
        var state = CharacterDragPhysics(position: CGPoint(x: 200, y: 0), pointer: CGPoint(x: 200, y: 120))
        for _ in 0..<90 { state.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600) }
        state.pointer = CGPoint(x: 280, y: 200)
        state.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600)
        XCTAssertLessThan(state.position.x, 280)
        XCTAssertLessThanOrEqual(hypot(state.position.x - 280, state.position.y - 200),
                                 CharacterDragPhysics.maximumRopeLength + 0.001)
        var crossedPin = false
        for _ in 0..<120 {
            state.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600)
            crossedPin = crossedPin || state.position.x > 280
            XCTAssertLessThanOrEqual(hypot(state.position.x - 280, state.position.y - 200),
                                     CharacterDragPhysics.maximumRopeLength + 0.001)
        }
        XCTAssertTrue(crossedPin, "The hanging body must swing past the pin, rather than stick to the pointer")
    }

    func testStationaryReleaseDropsWhileMovingReleasePreservesBallisticVelocity() {
        var resting = CharacterDragPhysics(position: CGPoint(x: 300, y: 252), pointer: CGPoint(x: 300, y: 300))
        for _ in 0..<90 { resting.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600) }
        let restingPosition = resting.position
        resting.release(tangentRange: 26...774, depth: 600)
        resting.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600)
        XCTAssertEqual(resting.position.x, restingPosition.x, accuracy: 0.001)
        XCTAssertLessThan(resting.position.y, restingPosition.y)

        var moving = CharacterDragPhysics(position: CGPoint(x: 300, y: 252), pointer: CGPoint(x: 300, y: 300))
        moving.pointer = CGPoint(x: 400, y: 380)
        moving.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600)
        let releasePosition = moving.position
        let releaseVelocity = moving.velocity
        XCTAssertGreaterThan(releaseVelocity.dx, 0)
        XCTAssertGreaterThan(releaseVelocity.dy, 0)
        moving.release(tangentRange: 26...774, depth: 600)
        XCTAssertEqual(moving.velocity, releaseVelocity)
        moving.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600)
        XCTAssertGreaterThan(moving.position.x, releasePosition.x)
        XCTAssertGreaterThan(moving.position.y, releasePosition.y)
        for _ in 0..<180 { moving.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600) }
        XCTAssertTrue(moving.landed)
    }

    func testFlickAndReleaseBetweenRenderFramesPreservesTheLastPointerImpulse() {
        var state = CharacterDragPhysics(position: CGPoint(x: 200, y: 152), pointer: CGPoint(x: 200, y: 200))
        state.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600)
        state.pointer = CGPoint(x: 350, y: 350)
        state.release(tangentRange: 26...774, depth: 600)
        XCTAssertGreaterThan(state.velocity.dx, 0)
        XCTAssertGreaterThan(state.velocity.dy, 0)
        let released = state.position
        state.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600)
        XCTAssertGreaterThan(state.position.x, released.x)
        XCTAssertGreaterThan(state.position.y, released.y)
    }

    func testDiagonalCeilingCollisionRespectsBothRopeLengthAndVisibleBounds() {
        var state = CharacterDragPhysics(position: CGPoint(x: 200, y: 530), pointer: CGPoint(x: 240, y: 590))
        for step in 0..<120 {
            state.pointer = CGPoint(x: step % 2 == 0 ? 100 : 700, y: 590)
            state.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600)
            let pin = state.pin(tangentRange: 26...774, depth: 600)!
            XCTAssertLessThanOrEqual(hypot(state.position.x - pin.x, state.position.y - pin.y),
                                     CharacterDragPhysics.maximumRopeLength + 0.001)
            XCTAssertTrue((26...774).contains(state.position.x))
            XCTAssertTrue((0...540).contains(state.position.y))
        }
    }

    func testReleaseFallsBouncesAndSettlesAtNewTrackPosition() {
        var state = CharacterDragPhysics(position: CGPoint(x: 500, y: 240),
                                         velocity: CGVector(dx: 80, dy: 0), pointer: CGPoint(x: 500, y: 288))
        state.release(tangentRange: 26...774, depth: 600)
        var bounced = false
        for _ in 0..<180 {
            state.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600)
            bounced = bounced || (state.pointer == nil && state.velocity.dy > 0)
        }
        XCTAssertTrue(bounced)
        XCTAssertTrue(state.landed)
        XCTAssertEqual(state.position.y, 0)
        XCTAssertGreaterThan(state.position.x, 500)
        XCTAssertEqual(state.velocity, .zero)
    }

    func testLargeFrameGapAndExtremePointerRemainBounded() {
        var state = CharacterDragPhysics(position: CGPoint(x: 50, y: 0), pointer: CGPoint(x: 1e8, y: 1e8))
        for _ in 0..<120 { state.step(deltaTime: 100, tangentRange: 26...774, depth: 600) }
        XCTAssertTrue(state.position.x.isFinite && state.position.y.isFinite)
        XCTAssertTrue((26...774).contains(state.position.x))
        XCTAssertTrue((0...540).contains(state.position.y))
        state.release(tangentRange: 26...774, depth: 600)
        for _ in 0..<180 { state.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600) }
        XCTAssertTrue(state.landed)
    }

    func testReduceMotionKeepsRepositioningWithoutSwingOrBounce() {
        var state = CharacterDragPhysics(position: CGPoint(x: 50, y: 0), pointer: CGPoint(x: 500, y: 200))
        state.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600, reduceMotion: true)
        XCTAssertEqual(state.position, CGPoint(x: 500, y: 152))
        XCTAssertEqual(state.rotation, 0)
        state.release(tangentRange: 26...774, depth: 600)
        for _ in 0..<120 {
            state.step(deltaTime: 1 / 30, tangentRange: 26...774, depth: 600, reduceMotion: true)
            XCTAssertLessThanOrEqual(state.velocity.dy, 0)
        }
        XCTAssertTrue(state.landed)
    }

    func testDragSuppressesClickEvenWhenPointerReturnsToOriginAndCancelNeverClicks() {
        var gesture = CharacterDragGesture()
        gesture.begin(at: .zero)
        XCTAssertFalse(gesture.move(to: CGPoint(x: 2, y: 2)))
        XCTAssertTrue(gesture.end())
        gesture.begin(at: .zero)
        XCTAssertTrue(gesture.move(to: CGPoint(x: 10, y: 10)))
        XCTAssertFalse(gesture.move(to: .zero))
        XCTAssertFalse(gesture.end())
        gesture.begin(at: .zero)
        gesture.cancel()
        XCTAssertFalse(gesture.end())
    }

    func testDragCoordinatesRoundTripOnAllEdges() {
        for edge in OverlayEdge.allCases {
            let geometry = EdgeTrackGeometry(bounds: CGRect(x: 80, y: 40, width: 600, height: 500), edge: edge)
            let drag = CGPoint(x: 200, y: 150)
            let result = geometry.dragPoint(for: geometry.worldPoint(forDrag: drag))
            XCTAssertEqual(result.x, drag.x, accuracy: 0.001)
            XCTAssertEqual(result.y, drag.y, accuracy: 0.001)
        }
    }

    func testViewportExpansionAndShrinkPreserveScreenPositionsAndPublishOnlyCoherentFrames() throws {
        let screen = OverlayScreenGeometry(identifier: "test", legacySignature: "test", name: "test",
            visibleFrame: CGRect(x: 120, y: 80, width: 1440, height: 900))
        for edge in OverlayEdge.allCases {
            for span in [OverlaySpan.half, .third] {
                let preference = OverlayRegionPreference(edge: edge, span: span, screenIdentifier: "test")
                let compact = OverlayRegionLayout.frames(for: preference, on: screen)
                let expanded = OverlayRegionFrames(activityFrame: compact.activityFrame, renderFrame: screen.visibleFrame)
                let room = UUID()
                let members = (0..<3).map { index in
                    PixelWorldMember(id: UUID(), nickname: "친구 \(index)", characterID: PixelCharacterCatalog.pixelHamsterID,
                        presence: index == 2 ? .away : .online, isTyping: false, isCurrentUser: index == 0)
                }
                var now: TimeInterval = 100
                var reports: [[UUID: CGRect]] = []
                let scene = PixelWorldScene(size: compact.renderFrame.size, clock: { now }, usesPlaybackClock: true)
                scene.apply(roomID: room, members: members, bubbles: [], edge: edge,
                    activityFrame: compact.localActivityFrame, installationSeed: 1,
                    onCharacterFramesChanged: { reports.append($0) })
                let middle = scene.trackGeometry.tangentLength / 2
                scene.beginCharacterDrag(memberID: members[0].id, pointer: CGPoint(x: middle - 40, y: 180))
                scene.beginCharacterDrag(memberID: members[1].id, pointer: CGPoint(x: middle + 40, y: 180))
                for _ in 0..<12 { now += 1 / 30; scene.update(now) }
                scene.endCharacterDrag(memberID: members[1].id)
                let event = CharacterThrowEvent(id: UUID(), roomID: room, actorUserID: members[2].id,
                    targetUserID: members[0].id, sourceCharacterID: PixelCharacterCatalog.pixelHamsterID)
                scene.apply(roomID: room, members: members, bubbles: [], edge: edge,
                    activityFrame: compact.localActivityFrame, installationSeed: 1, characterThrow: event,
                    onCharacterFramesChanged: { reports.append($0) })
                now += PixelCharacterThrowStyle.releaseDelay + 0.02
                scene.update(now)
                XCTAssertEqual(scene.activeProjectileCount, 1)
                XCTAssertEqual(scene.draggingMemberIDs, Set(members.prefix(2).map(\.id)))
                let originalPositions = try members.map { member in
                    screenPoint(try XCTUnwrap(scene.renderedCharacterPosition(for: member.id)), in: compact.renderFrame)
                }
                let originalProjectile = screenPoint(try XCTUnwrap(scene.renderedProjectilePositions.first), in: compact.renderFrame)
                let originalPin = screenPoint(try XCTUnwrap(scene.renderedDragPinPosition), in: compact.renderFrame)
                let originalRope = try XCTUnwrap(scene.renderedDragRopeBounds)
                    .offsetBy(dx: compact.renderFrame.minX, dy: compact.renderFrame.minY)
                for viewport in [expanded, compact] {
                    reports.removeAll()
                    scene.setViewport(size: viewport.renderFrame.size, activityFrame: viewport.localActivityFrame)
                    XCTAssertEqual(reports.count, 1, "Viewport must not report its intermediate resize state: \(edge)/\(span)")
                    let frames = try XCTUnwrap(reports.first)
                    for (index, member) in members.enumerated() {
                        let reported = try XCTUnwrap(frames[member.id])
                        assertPoint(screenPoint(CGPoint(x: reported.midX, y: reported.midY), in: viewport.renderFrame),
                                    equals: originalPositions[index])
                        assertPoint(screenPoint(try XCTUnwrap(scene.renderedCharacterPosition(for: member.id)),
                                                in: viewport.renderFrame), equals: originalPositions[index])
                    }
                    assertPoint(screenPoint(try XCTUnwrap(scene.renderedDragPinPosition), in: viewport.renderFrame), equals: originalPin)
                    let rope = try XCTUnwrap(scene.renderedDragRopeBounds)
                        .offsetBy(dx: viewport.renderFrame.minX, dy: viewport.renderFrame.minY)
                    assertPoint(rope.origin, equals: originalRope.origin)
                    XCTAssertEqual(rope.width, originalRope.width, accuracy: 0.001)
                    XCTAssertEqual(rope.height, originalRope.height, accuracy: 0.001)
                    // Recompute the projectile at the same instant to check its
                    // trajectory origin, not just the currently displayed node.
                    scene.update(now)
                    assertPoint(screenPoint(try XCTUnwrap(scene.renderedProjectilePositions.first),
                                            in: viewport.renderFrame), equals: originalProjectile)
                    XCTAssertEqual(scene.draggingMemberIDs, Set(members.prefix(2).map(\.id)))
                    reports.removeAll()
                    scene.setViewport(size: viewport.renderFrame.size, activityFrame: viewport.localActivityFrame)
                    XCTAssertTrue(reports.isEmpty, "An unchanged viewport must not publish another layout")
                }
            }
        }
    }

    func testResizeAndActivityOriginUpdateNeverGroundAHeldCharacter() throws {
        let member = PixelWorldMember(id: UUID(), nickname: "친구", characterID: PixelCharacterCatalog.pixelHamsterID,
            presence: .online, isTyping: false, isCurrentUser: true)
        let room = UUID()
        let frame = CGRect(x: 100, y: 0, width: 500, height: 240)
        let scene = PixelWorldScene(size: CGSize(width: 700, height: 360))
        scene.apply(roomID: room, members: [member], bubbles: [], edge: .bottom, activityFrame: frame, installationSeed: 1)
        scene.beginCharacterDrag(memberID: member.id, pointer: CGPoint(x: 250, y: 180))
        scene.update(1)
        let held = try XCTUnwrap(scene.renderedCharacterPosition(for: member.id))
        XCTAssertGreaterThan(held.y, EdgeTrackGeometry.footInset)
        scene.size = CGSize(width: 900, height: 600)
        assertPoint(try XCTUnwrap(scene.renderedCharacterPosition(for: member.id)), equals: held)
        scene.apply(roomID: room, members: [member], bubbles: [], edge: .bottom,
            activityFrame: frame.offsetBy(dx: 60, dy: 40), installationSeed: 1)
        assertPoint(try XCTUnwrap(scene.renderedCharacterPosition(for: member.id)),
                    equals: CGPoint(x: held.x + 60, y: held.y + 40))
    }

    private func screenPoint(_ local: CGPoint, in renderFrame: CGRect) -> CGPoint {
        CGPoint(x: renderFrame.minX + local.x, y: renderFrame.minY + local.y)
    }

    private func assertPoint(_ actual: CGPoint, equals expected: CGPoint,
                             file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(actual.x, expected.x, accuracy: 0.001, file: file, line: line)
        XCTAssertEqual(actual.y, expected.y, accuracy: 0.001, file: file, line: line)
    }

    func testRoomSwitchAndMemberRemovalCancelLocalDragAndSuppressionTracksMemberState() {
        let id = UUID()
        let scene = PixelWorldScene(size: CGSize(width: 800, height: 600))
        let room = UUID()
        var member = PixelWorldMember(id: id, nickname: "친구", characterID: "hamster", presence: .online,
                                      isTyping: false, isCurrentUser: false, overlaySuppressed: true)
        scene.apply(roomID: room, members: [member], bubbles: [], edge: .bottom, installationSeed: 1)
        XCTAssertTrue(scene.renderedOverlaySuppressed(for: id))
        scene.beginCharacterDrag(memberID: id, pointer: CGPoint(x: 300, y: 250))
        XCTAssertEqual(scene.draggingMemberIDs, [id])
        scene.apply(roomID: room, members: [], bubbles: [], edge: .bottom, installationSeed: 1)
        XCTAssertTrue(scene.draggingMemberIDs.isEmpty)
        member = PixelWorldMember(id: id, nickname: "친구", characterID: "hamster", presence: .online,
                                  isTyping: false, isCurrentUser: false, overlaySuppressed: false)
        scene.apply(roomID: room, members: [member], bubbles: [], edge: .bottom, installationSeed: 1)
        XCTAssertFalse(scene.renderedOverlaySuppressed(for: id))
        scene.beginCharacterDrag(memberID: id, pointer: CGPoint(x: 300, y: 250))
        scene.apply(roomID: UUID(), members: [member], bubbles: [], edge: .bottom, installationSeed: 1)
        XCTAssertTrue(scene.draggingMemberIDs.isEmpty)
    }
}
