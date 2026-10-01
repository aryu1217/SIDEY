import Foundation
import CoreGraphics

/// Coordinates are local to the edge: x follows the track, y points into the screen.
/// No pointer samples leave the local overlay or survive its lifetime.
struct CharacterDragPhysics {
    static let maximumRopeLength: CGFloat = 48
    var position: CGPoint
    var velocity: CGVector = .zero
    var pointer: CGPoint?
    var rotation: CGFloat = 0
    private var accumulator: TimeInterval = 0
    private(set) var landed = false
    private var flightTime: TimeInterval = 0
    private var lastSimulatedPointer: CGPoint?

    init(position: CGPoint, velocity: CGVector = .zero, pointer: CGPoint? = nil) {
        self.position = position
        self.velocity = velocity
        self.pointer = pointer
    }

    mutating func release(tangentRange: ClosedRange<CGFloat>, depth: CGFloat, reduceMotion: Bool = false) {
        // Mouse-up may arrive before the next render. Apply the final pin movement
        // once so a quick flick still transfers its impulse into the released body.
        if pointer != nil, pointer != lastSimulatedPointer {
            step(deltaTime: 1 / 120, tangentRange: tangentRange, depth: depth, reduceMotion: reduceMotion)
        }
        pointer = nil
    }

    func pin(tangentRange: ClosedRange<CGFloat>, depth: CGFloat) -> CGPoint? {
        pointer.map { CGPoint(x: min(max($0.x, tangentRange.lowerBound), tangentRange.upperBound),
                              y: min(max($0.y, 0), max(0, depth - 22))) }
    }

    mutating func step(deltaTime: TimeInterval, tangentRange: ClosedRange<CGFloat>, depth: CGFloat,
                       reduceMotion: Bool = false) {
        guard !landed else { return }
        accumulator += min(max(deltaTime.isFinite ? deltaTime : 0, 0), 0.1)
        let dt: CGFloat = 1 / 120
        while accumulator >= Double(dt) {
            accumulator -= Double(dt)
            let ceiling = max(0, depth - 60)
            let previous = position
            lastSimulatedPointer = pointer
            let pin = pin(tangentRange: tangentRange, depth: depth)
            if let pin, reduceMotion {
                position = CGPoint(x: pin.x, y: max(0, pin.y - Self.maximumRopeLength))
                velocity = .zero
                rotation = 0
                continue
            }
            velocity.dy -= 1100 * dt
            velocity.dx *= exp(-(pin == nil ? 2.8 : 0.75) * dt)
            velocity.dy *= exp(-(pin == nil ? 0 : 0.75) * dt)
            if pin == nil {
                flightTime += Double(dt)
                rotation *= exp(-7 * dt)
            }
            velocity.dx = max(-650, min(650, velocity.dx))
            velocity.dy = max(-850, min(850, velocity.dy))
            position.x += velocity.dx * dt
            position.y += velocity.dy * dt
            if let pin {
                let dx = position.x - pin.x
                let dy = position.y - pin.y
                let distance = hypot(dx, dy)
                if distance > Self.maximumRopeLength {
                    let ratio = Self.maximumRopeLength / distance
                    position = CGPoint(x: pin.x + dx * ratio, y: pin.y + dy * ratio)
                    // Constraint displacement transfers the moving pin's momentum.
                    velocity.dx = min(650, max(-650, (position.x - previous.x) / dt))
                    velocity.dy = min(850, max(-850, (position.y - previous.y) / dt))
                }
                rotation = max(-0.9, min(0.9, -atan2(pin.x - position.x, max(1, pin.y - position.y))))
            }
            if position.x < tangentRange.lowerBound || position.x > tangentRange.upperBound {
                position.x = min(max(position.x, tangentRange.lowerBound), tangentRange.upperBound)
                velocity.dx *= -0.15
            }
            if position.y > ceiling { position.y = ceiling; velocity.dy = min(0, velocity.dy) }
            if let pin {
                // Project into the intersection of the visible box and the rope
                // disc. Clamping only after the radial projection stretches the
                // rope at a diagonal ceiling collision.
                position.y = min(max(position.y, max(0, pin.y - Self.maximumRopeLength)), ceiling)
                let vertical = position.y - pin.y
                let horizontal = sqrt(max(0, Self.maximumRopeLength * Self.maximumRopeLength - vertical * vertical))
                let lower = max(tangentRange.lowerBound, pin.x - horizontal)
                let upper = min(tangentRange.upperBound, pin.x + horizontal)
                position.x = min(max(position.x, lower), upper)
                velocity.dx = min(650, max(-650, (position.x - previous.x) / dt))
                velocity.dy = min(850, max(-850, (position.y - previous.y) / dt))
            }
            if position.y <= 0 {
                position.y = 0
                if pointer == nil {
                    if reduceMotion || abs(velocity.dy) < 95 || flightTime > 3 {
                        velocity = .zero; rotation = 0; landed = true
                    } else {
                        velocity.dy = min(160, abs(velocity.dy) * 0.24)
                        velocity.dx *= 0.6
                    }
                } else { velocity.dy = max(0, velocity.dy) }
            }
        }
    }
}

struct CharacterDragGesture {
    private(set) var origin: CGPoint?
    private(set) var isDragging = false
    mutating func begin(at point: CGPoint) { origin = point; isDragging = false }
    /// True only when this event first crosses the deliberate-drag threshold.
    mutating func move(to point: CGPoint) -> Bool {
        guard let origin, !isDragging, hypot(point.x - origin.x, point.y - origin.y) >= 5 else { return false }
        isDragging = true
        return true
    }
    /// Returns whether the original press should still perform its click action.
    mutating func end() -> Bool {
        let clicks = origin != nil && !isDragging
        origin = nil; isDragging = false
        return clicks
    }
    mutating func cancel() { origin = nil; isDragging = false }
}

@MainActor
final class CharacterDragBridge {
    weak var scene: PixelWorldScene?
    weak var renderView: PixelWorldRenderView?
    private(set) var viewport: (size: CGSize, activityFrame: CGRect)?
    private(set) var composerFrame: CGRect?
    var onActivityChanged: ((Bool) -> Void)?
    func setViewport(size: CGSize, activityFrame: CGRect) {
        viewport = (size, activityFrame)
        renderView?.synchronizeViewport()
    }
    func setComposerFrame(_ frame: CGRect) {
        composerFrame = frame
        scene?.setComposerFrame(frame)
    }
    func begin(memberID: UUID, pointer: CGPoint) { scene?.beginCharacterDrag(memberID: memberID, pointer: pointer) }
    func move(memberID: UUID, pointer: CGPoint) { scene?.moveCharacterDrag(memberID: memberID, pointer: pointer) }
    func end(memberID: UUID) { scene?.endCharacterDrag(memberID: memberID) }
    func cancel() { scene?.cancelCharacterDrags() }
}

extension EdgeTrackGeometry {
    func dragPoint(for point: CGPoint) -> CGPoint {
        switch edge {
        case .bottom: CGPoint(x: point.x - bounds.minX, y: point.y - bounds.minY - Self.footInset)
        case .top: CGPoint(x: point.x - bounds.minX, y: bounds.maxY - Self.footInset - point.y)
        case .left: CGPoint(x: point.y - bounds.minY, y: point.x - bounds.minX - Self.footInset)
        case .right: CGPoint(x: point.y - bounds.minY, y: bounds.maxX - Self.footInset - point.x)
        }
    }
    func worldPoint(forDrag point: CGPoint) -> CGPoint {
        let ground = self.point(for: point.x)
        return switch edge {
        case .bottom: CGPoint(x: ground.x, y: ground.y + point.y)
        case .top: CGPoint(x: ground.x, y: ground.y - point.y)
        case .left: CGPoint(x: ground.x + point.y, y: ground.y)
        case .right: CGPoint(x: ground.x - point.y, y: ground.y)
        }
    }
}
