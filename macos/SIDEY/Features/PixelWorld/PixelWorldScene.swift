import AppKit
import SpriteKit

enum PresenceIndicatorTone: Equatable {
    case green
    case orange
    case red
    case gray

    static func tone(for presence: PresenceState) -> Self {
        switch presence {
        case .online, .typing: .green
        case .away: .orange
        case .offline: .red
        case .reconnecting: .gray
        }
    }

    var color: NSColor {
        switch self {
        case .green: .systemGreen
        case .orange: .systemOrange
        case .red: .systemRed
        case .gray: .systemGray
        }
    }
}

enum PixelWorldRendererPolicy {
    static let preferredFramesPerSecond = 30

    @MainActor static func apply(to view: SKView) {
        view.preferredFramesPerSecond = preferredFramesPerSecond
        view.allowsTransparency = true
        view.ignoresSiblingOrder = true
        view.shouldCullNonVisibleNodes = true
        view.isAsynchronous = true
    }
}

struct PixelCharacterVisualState: Equatable {
    let motion: PixelCharacterMotion
    let alpha: CGFloat
    let colorBlendFactor: CGFloat
    let showsDozeLabel: Bool
    let facingScale: CGFloat
}

enum PixelDozeLabelStyle {
    static var text: String { L10n.text("pixel_world.doze_label") }
    static let fontSize: CGFloat = 14
    static let outlineWidth: CGFloat = 2
    static let restingAlpha: CGFloat = 0.55
    static let floatingDistance: CGFloat = 3
    static let fillColor = NSColor.systemOrange
    static let outlineColor = NSColor(srgbRed: 0.08, green: 0.07, blue: 0.06, alpha: 0.92)

    static let outlineOffsets: [CGPoint] = (0..<12).map { index in
        let angle = CGFloat(index) * .pi * 2 / 12
        return CGPoint(
            x: cos(angle) * outlineWidth,
            y: sin(angle) * outlineWidth
        )
    }
}

enum PixelCharacterPulseStyle {
    static let peakScale: CGFloat = 7
    static let growDuration: TimeInterval = 0.20
    static let settleDuration: TimeInterval = 0.60
    static let totalDuration = growDuration + settleDuration
}

struct PixelWorldRenderingConfiguration: Equatable, Sendable {
    let pulsePeakScale: CGFloat
    let initialTrackFractions: [UUID: CGFloat]
    let fixedTrackFractions: [UUID: CGFloat]
    let minimumTrackInset: CGFloat
    let allowsLocalPreviewEvents: Bool

    static let live = Self(
        pulsePeakScale: PixelCharacterPulseStyle.peakScale,
        initialTrackFractions: [:],
        fixedTrackFractions: [:],
        minimumTrackInset: EdgeTrackGeometry.hotspotPointSize / 2,
        allowsLocalPreviewEvents: false
    )

    static func storePreview(
        initialTrackFractions: [UUID: CGFloat] = [:],
        fixedTrackFractions: [UUID: CGFloat]
    ) -> Self {
        Self(
            pulsePeakScale: 3,
            initialTrackFractions: initialTrackFractions,
            fixedTrackFractions: fixedTrackFractions,
            minimumTrackInset: EdgeTrackGeometry.characterPointSize * 1.5,
            allowsLocalPreviewEvents: true
        )
    }
}

enum PixelWorldRenderEvent: Equatable {
    case pulse(UUID)
    case throwStarted(UUID)
    case projectileReleased(UUID)
    case impact(UUID)
    case hit(UUID)
}

enum PixelCharacterFacingPolicy {
    static let movementThreshold: CGFloat = 2

    static func scale(
        mirrorsToMovementDirection: Bool,
        velocity: CGFloat,
        edge: OverlayEdge,
        previousScale: CGFloat
    ) -> CGFloat {
        guard mirrorsToMovementDirection,
              velocity.isFinite,
              abs(velocity) > movementThreshold
        else { return previousScale }

        let positiveTangentScale: CGFloat = switch edge {
        case .bottom, .right: 1
        case .top, .left: -1
        }
        return velocity > 0 ? positiveTangentScale : -positiveTangentScale
    }
}

enum PixelSparkleVisibilityPolicy {
    static func showsAmbient(for presence: PresenceState) -> Bool {
        presence == .online || presence == .typing
    }
}

enum PixelSparkleStyle {
    static let ambientActionKey = "pixel-character-ambient-sparkles"
    static let pulseActionKey = "pixel-character-pulse-sparkles"
    static func starPath() -> CGPath {
        let path = CGMutablePath()
        path.move(to: CGPoint(x: 0, y: 3))
        path.addLine(to: CGPoint(x: 1, y: 1))
        path.addLine(to: CGPoint(x: 3, y: 0))
        path.addLine(to: CGPoint(x: 1, y: -1))
        path.addLine(to: CGPoint(x: 0, y: -3))
        path.addLine(to: CGPoint(x: -1, y: -1))
        path.addLine(to: CGPoint(x: -3, y: 0))
        path.addLine(to: CGPoint(x: -1, y: 1))
        path.closeSubpath()
        return path
    }
}

private struct CharacterPulseKey: Hashable {
    let roomID: UUID
    let userID: UUID
}

private struct ActiveCharacterProjectile {
    let event: CharacterThrowEvent
    let node: SKSpriteNode
    let startedAt: TimeInterval
    let flightDuration: TimeInterval
    var startPoint: CGPoint
    let inwardArcHeight: CGFloat
    var hasReleased = false
    var playsSound = true
}

final class PixelWorldScene: SKScene {
    private let clock: () -> TimeInterval
    var onCharacterImpact: ((String, TimeInterval) -> Void)?
    var onStopCharacterSounds: (() -> Void)?
    private(set) var stunState = CharacterStunState()
    private var stunRealtimeAvailable: Bool?

    func useStunState(_ state: CharacterStunState, realtimeAvailable: Bool) {
        stunState = state
        if let previous = stunRealtimeAvailable, previous != realtimeAvailable {
            resetStunState()
            projectiles.forEach { $0.node.removeFromParent() }
            projectiles.removeAll()
            removeImpactEffects()
            hitUntil.removeAll()
        }
        stunRealtimeAvailable = realtimeAvailable
    }

    func resetStunState() {
        onStopCharacterSounds?()
        stunState.reset()
        for node in characterNodes.values { node.updateStun(startedAt: nil, time: clock()) }
    }

    func renderedStunStarCount(for id: UUID) -> Int {
        characterNodes[id]?.stunStarCount ?? 0
    }
    private let renderingConfiguration: PixelWorldRenderingConfiguration
    private var characterNodes: [UUID: PixelCharacterNode] = [:]
    private var agents: [UUID: PixelMovementAgent] = [:]
    private var characterDrags: [UUID: CharacterDragPhysics] = [:]
    private let dragRope = SKShapeNode()
    private let dragPin = SKShapeNode(circleOfRadius: 3)
    weak var dragBridge: CharacterDragBridge?

    func beginCharacterDrag(memberID: UUID, pointer: CGPoint) {
        guard let agent = agents[memberID], suspendedReasons.isEmpty,
              pointer.x.isFinite, pointer.y.isFinite else { return }
        let wasEmpty = characterDrags.isEmpty
        if dragRope.parent == nil {
            dragRope.strokeColor = NSColor.white.withAlphaComponent(0.85)
            dragRope.lineWidth = 1.5
            dragRope.zPosition = 100
            dragPin.fillColor = .white
            dragPin.strokeColor = NSColor.black.withAlphaComponent(0.5)
            dragPin.zPosition = 101
            dragRope.isHidden = true
            dragPin.isHidden = true
            addChild(dragRope)
            addChild(dragPin)
        }
        characterDrags[memberID] = CharacterDragPhysics(
            position: characterDrags[memberID]?.position ?? CGPoint(x: agent.trackPosition, y: 0), pointer: pointer)
        if wasEmpty { dragBridge?.onActivityChanged?(true) }
    }

    func moveCharacterDrag(memberID: UUID, pointer: CGPoint) {
        guard pointer.x.isFinite, pointer.y.isFinite, characterDrags[memberID]?.pointer != nil else { return }
        characterDrags[memberID]?.pointer = pointer
    }

    func endCharacterDrag(memberID: UUID) {
        characterDrags[memberID]?.release(tangentRange: trackGeometry.trackRange,
            depth: edge.isHorizontal ? size.height : size.width,
            reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion)
        dragRope.isHidden = true
        dragPin.isHidden = true
    }

    func cancelCharacterDrags() {
        guard !characterDrags.isEmpty else { return }
        for (id, drag) in characterDrags {
            agents[id]?.trackPosition = trackGeometry.clamped(drag.position.x)
            agents[id]?.target = trackGeometry.clamped(drag.position.x)
            agents[id]?.velocity = 0
            characterNodes[id]?.position = trackGeometry.point(for: drag.position.x)
            characterNodes[id]?.setDragRotation(0)
        }
        characterDrags.removeAll()
        dragRope.isHidden = true
        dragPin.isHidden = true
        dragBridge?.onActivityChanged?(false)
    }

    var draggingMemberIDs: Set<UUID> { Set(characterDrags.keys) }
    func renderedOverlaySuppressed(for memberID: UUID) -> Bool {
        characterNodes[memberID]?.showsOverlaySuppressed ?? false
    }

    private var members: [UUID: PixelWorldMember] = [:]
    private var stationaryTreeIDs: Set<UUID> = []
    private var activeBubbles: [UUID: [ActiveBubble]] = [:]
    private var lastPulseEventIDs: [CharacterPulseKey: UUID] = [:]
    private var recentThrowEventIDs: [UUID] = []
    private var recentThrowEventIDSet: Set<UUID> = []
    private var projectiles: [ActiveCharacterProjectile] = []
    private var hitUntil: [UUID: TimeInterval] = [:]
    private var currentRoomID: UUID?
    private var installationSeed: UInt64 = 0
    private var edge: OverlayEdge = .bottom
    private var activityFrame: CGRect?
    private var isUpdatingViewport = false
    private var composerVisible = false
    private var composerFrame: CGRect?
    private final class Resources {
        var lifecycleObservers: [(NotificationCenter, NSObjectProtocol)] = []
    }

    private let resourceLifetime = MainActorResourceLifetime(Resources()) { resources in
        for (center, token) in resources.lifecycleObservers { center.removeObserver(token) }
    }
    private var suspendedReasons: Set<String> = []
    var isRenderingSuspended: Bool { !suspendedReasons.isEmpty }
    private var lastUpdateTime: TimeInterval?
    private var lastHotspotReportTime: TimeInterval = 0
    private var lastHotspotFrames: [UUID: CGRect] = [:]
    private var lastChatBubbleFrames: [UUID: CGRect] = [:]
    private var onCurrentUserFrameChanged: ((CGRect?) -> Void)?
    private var onCharacterFramesChanged: (([UUID: CGRect]) -> Void)?
    private var onChatBubbleFramesChanged: (([UUID: CGRect]) -> Void)?
    private var lastLocalPulseTimes: [UUID: TimeInterval] = [:]
    private(set) var previewRenderEvents: [PixelWorldRenderEvent] = []

    private let usesPlaybackClock: Bool

    init(
        size: CGSize,
        renderingConfiguration: PixelWorldRenderingConfiguration = .live,
        clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
        usesPlaybackClock: Bool = false
    ) {
        self.clock = clock
        self.usesPlaybackClock = usesPlaybackClock
        self.renderingConfiguration = renderingConfiguration
        super.init(size: size)
        scaleMode = .resizeFill
        backgroundColor = .clear
        anchorPoint = .zero
        let workspace = NSWorkspace.shared.notificationCenter
        let distributed = DistributedNotificationCenter.default()
        let events: [(NotificationCenter, Notification.Name, String, Bool)] = [
            (workspace, NSWorkspace.willSleepNotification, "sleep", true),
            (workspace, NSWorkspace.didWakeNotification, "sleep", false),
            (workspace, NSWorkspace.screensDidSleepNotification, "display", true),
            (workspace, NSWorkspace.screensDidWakeNotification, "display", false),
            (workspace, NSWorkspace.sessionDidResignActiveNotification, "session", true),
            (workspace, NSWorkspace.sessionDidBecomeActiveNotification, "session", false),
            (distributed, Notification.Name("com.apple.screenIsLocked"), "lock", true),
            (distributed, Notification.Name("com.apple.screenIsUnlocked"), "lock", false)
        ]
        for (center, name, reason, suspended) in events {
            let token = center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.setSuspended(suspended, reason: reason) }
            }
            resourceLifetime.resource.lifecycleObservers.append((center, token))
        }
    }

    nonisolated deinit {}

    func setSuspended(_ suspended: Bool, reason: String) {
        if suspended { cancelCharacterDrags() }
        if suspended { suspendedReasons.insert(reason) } else { suspendedReasons.remove(reason) }
        resetStunState()
        projectiles.forEach { $0.node.removeFromParent() }
        projectiles.removeAll()
        removeImpactEffects()
        hitUntil.removeAll()
        lastUpdateTime = nil
    }

    required init?(coder aDecoder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func setComposerFrame(_ frame: CGRect) {
        composerFrame = frame
    }

    /// A viewport resize changes render coordinates, not the local simulation.
    /// Publish frames only after both inputs and every scene-space anchor agree.
    func setViewport(size requestedSize: CGSize, activityFrame requestedFrame: CGRect,
                     reportFrames: Bool = true) {
        guard requestedSize.width.isFinite, requestedSize.height.isFinite,
              requestedSize.width > 0, requestedSize.height > 0,
              requestedFrame.origin.x.isFinite, requestedFrame.origin.y.isFinite,
              requestedFrame.width.isFinite, requestedFrame.height.isFinite,
              requestedFrame.width > 0, requestedFrame.height > 0 else { return }
        guard size != requestedSize || activityFrame != requestedFrame else { return }
        let previousFrame = effectiveActivityFrame
        let trackChanged = previousFrame.size != requestedFrame.size
        if trackChanged { cancelCharacterDrags() }
        isUpdatingViewport = true
        activityFrame = requestedFrame
        size = requestedSize
        if !trackChanged {
            rebaseScene(by: CGVector(dx: requestedFrame.minX - previousFrame.minX,
                                    dy: requestedFrame.minY - previousFrame.minY))
        } else {
            layoutCharacters(needsInitialLayout: false)
        }
        isUpdatingViewport = false
        if reportFrames { reportCharacterFrames(force: true) }
    }

    private func rebaseScene(by offset: CGVector) {
        guard offset.dx != 0 || offset.dy != 0 else { return }
        // Character children carry their labels/effects with them. Scene children
        // also include projectiles, impact effects and the cursor pin.
        for node in children where node !== dragRope {
            node.position = CGPoint(x: node.position.x + offset.dx, y: node.position.y + offset.dy)
        }
        if let path = dragRope.path {
            var transform = CGAffineTransform(translationX: offset.dx, y: offset.dy)
            dragRope.path = path.copy(using: &transform)
        }
        for index in projectiles.indices {
            projectiles[index].startPoint.x += offset.dx
            projectiles[index].startPoint.y += offset.dy
        }
    }

    override func didChangeSize(_ oldSize: CGSize) {
        super.didChangeSize(oldSize)
        guard !isUpdatingViewport else { return }
        let needsInitialLayout = (oldSize.width <= 1 || oldSize.height <= 1)
            && size.width > 1 && size.height > 1
        layoutCharacters(needsInitialLayout: needsInitialLayout)
        reportCharacterFrames(force: true)
    }

    private func layoutCharacters(needsInitialLayout: Bool) {
        let geometry = trackGeometry
        for id in Array(agents.keys) {
            guard var agent = agents[id] else { continue }
            if needsInitialLayout {
                agent.trackPosition = initialTrackPosition(roomID: currentRoomID, userID: id)
                agent.target = initialTarget(roomID: currentRoomID, userID: id)
            } else {
                agent.trackPosition = configuredTrackPosition(agent.trackPosition, userID: id)
                agent.target = configuredTrackPosition(agent.target, userID: id)
            }
            agents[id] = agent
            characterNodes[id]?.position = characterDrags[id].map { geometry.worldPoint(forDrag: $0.position) }
                ?? geometry.point(for: agent.trackPosition)
        }
    }

    func apply(
        roomID: UUID?,
        members requestedMembers: [PixelWorldMember],
        bubbles: [ActiveBubble],
        edge: OverlayEdge,
        activityFrame: CGRect? = nil,
        installationSeed: UInt64,
        composerVisible: Bool = false,
        composerFrame: CGRect? = nil,
        characterPulse: CharacterPulseEvent? = nil,
        characterThrow: CharacterThrowEvent? = nil,
        onCurrentUserFrameChanged: ((CGRect?) -> Void)? = nil,
        onCharacterFramesChanged: (([UUID: CGRect]) -> Void)? = nil,
        onChatBubbleFramesChanged: (([UUID: CGRect]) -> Void)? = nil,
        pausedTreeUserIDs: Set<UUID>? = nil
    ) {
        self.onCurrentUserFrameChanged = onCurrentUserFrameChanged
        self.onCharacterFramesChanged = onCharacterFramesChanged
        self.onChatBubbleFramesChanged = onChatBubbleFramesChanged
        let roomChanged = roomID != currentRoomID
        let edgeChanged = self.edge != edge
        if roomChanged || edgeChanged || (self.activityFrame?.size != activityFrame?.size) {
            cancelCharacterDrags()
        }
        currentRoomID = roomID
        self.installationSeed = installationSeed
        self.edge = edge
        let activityFrameChanged = self.activityFrame != activityFrame
        if let activityFrame, size.width > 0, size.height > 0 {
            setViewport(size: size, activityFrame: activityFrame, reportFrames: false)
        } else {
            self.activityFrame = activityFrame
        }
        self.composerVisible = composerVisible
        self.composerFrame = composerFrame
        if roomChanged {
            stationaryTreeIDs.removeAll()
            resetStunState()
            characterNodes.values.forEach { $0.removeFromParent() }
            characterNodes.removeAll()
            agents.removeAll()
            lastHotspotFrames.removeAll()
            lastChatBubbleFrames.removeAll()
            projectiles.forEach { $0.node.removeFromParent() }
            projectiles.removeAll()
            removeImpactEffects()
            hitUntil.removeAll()
            lastLocalPulseTimes.removeAll()
            previewRenderEvents.removeAll()
        }

        let requestedByID = Dictionary(uniqueKeysWithValues: requestedMembers.map { ($0.id, $0) })
        let removedIDs = Set(characterNodes.keys).subtracting(requestedByID.keys)
        for id in removedIDs {
            characterNodes.removeValue(forKey: id)?.removeFromParent()
            agents.removeValue(forKey: id)
            if let removedDrag = characterDrags.removeValue(forKey: id) {
                if removedDrag.pointer != nil {
                    dragRope.isHidden = true
                    dragPin.isHidden = true
                }
                if characterDrags.isEmpty { dragBridge?.onActivityChanged?(false) }
            }
            hitUntil.removeValue(forKey: id)
            stunState.remove(id)
        }

        members = requestedByID
        stationaryTreeIDs = (pausedTreeUserIDs ?? stationaryTreeIDs).filter { members[$0]?.characterID == PixelCharacterCatalog.pixelTreeID }
        activeBubbles = Dictionary(grouping: bubbles, by: \.senderID).mapValues { senderBubbles in
            senderBubbles.sorted {
                $0.expiresAt == $1.expiresAt
                    ? $0.messageID.uuidString < $1.messageID.uuidString
                    : $0.expiresAt < $1.expiresAt
            }
        }
        let geometry = trackGeometry
        for member in requestedMembers {
            let node: PixelCharacterNode
            if let existing = characterNodes[member.id] {
                node = existing
            } else {
                node = PixelCharacterNode(memberID: member.id, characterID: member.characterID)
                characterNodes[member.id] = node
                addChild(node)
                let initial = initialTrackPosition(roomID: roomID, userID: member.id)
                let target = initialTarget(roomID: roomID, userID: member.id)
                agents[member.id] = PixelMovementAgent(
                    id: member.id,
                    trackPosition: initial,
                    target: target,
                    idleRemaining: stableUnit(roomID: roomID, userID: member.id, salt: 2) * 1.5
                )
                node.position = geometry.point(for: initial)
            }
            let tangent = agents[member.id]?.trackPosition ?? geometry.trackRange.lowerBound
            node.apply(
                member: member,
                bubbles: activeBubbles[member.id] ?? [],
                edge: edge,
                tangentPosition: tangent,
                tangentLength: geometry.tangentLength,
                inwardNormalLength: bubbleInwardNormalLength
            )
        }
        if edgeChanged || (activityFrameChanged && activityFrame == nil) {
            layoutCharacters(needsInitialLayout: false)
        }
        if let characterPulse,
           characterPulse.roomID == roomID,
           let node = characterNodes[characterPulse.userID] {
            let key = CharacterPulseKey(roomID: characterPulse.roomID, userID: characterPulse.userID)
            if lastPulseEventIDs[key] != characterPulse.id {
                lastPulseEventIDs[key] = characterPulse.id
                node.playPulse(peakScale: renderingConfiguration.pulsePeakScale)
                recordPreviewEvent(.pulse(characterPulse.userID))
            }
        }
        if let characterThrow, characterThrow.roomID == roomID {
            beginThrow(characterThrow)
        }
        reportCharacterFrames(force: true)
    }

    override func update(_ rendererTime: TimeInterval) {
        guard suspendedReasons.isEmpty else { return }
        let currentTime = usesPlaybackClock ? clock() : rendererTime
        let deltaTime = lastUpdateTime.map { currentTime - $0 } ?? (1.0 / 30.0)
        lastUpdateTime = currentTime
        hitUntil = hitUntil.filter { $0.value > currentTime }
        stunState.advance(to: currentTime)
        for (id, node) in characterNodes {
            node.updateStun(startedAt: stunState.startedAt[id], time: currentTime)
        }
        var stoppedIDs = PixelMovementPolicy.stoppedMemberIDs(in: members.values)
            .union(renderingConfiguration.fixedTrackFractions.keys)
            .union(hitUntil.keys)
            .union(stationaryTreeIDs)
        stoppedIDs.formUnion(stunState.startedAt.keys)
        stoppedIDs.formUnion(characterDrags.keys)
        let geometry = trackGeometry
        var orderedAgents = agents.values.sorted { $0.id.uuidString < $1.id.uuidString }
        PixelMovementSimulation.step(
            agents: &orderedAgents,
            deltaTime: deltaTime,
            geometry: geometry,
            avoidanceRects: composerAvoidanceRects,
            stoppedIDs: stoppedIDs,
            messageBubbleTangentRanges: messageBubbleTangentRanges
        )

        for var agent in orderedAgents {
            guard let node = characterNodes[agent.id], let member = members[agent.id] else { continue }
            if renderingConfiguration.fixedTrackFractions[agent.id] != nil {
                agent.trackPosition = configuredTrackPosition(agent.trackPosition, userID: agent.id)
                agent.target = agent.trackPosition
                agent.velocity = 0
            }
            if !stoppedIDs.contains(agent.id), abs(agent.trackPosition - agent.target) < 3 {
                agent.idleRemaining = 0.8 + stableUnit(
                    roomID: currentRoomID,
                    userID: agent.id,
                    salt: UInt64(currentTime.rounded(.down)) &+ 17
                ) * 2.2
                agent.target = randomTarget(for: agent.id, time: currentTime)
            }
            if var drag = characterDrags[agent.id] {
                let depth = edge.isHorizontal ? size.height : size.width
                drag.step(deltaTime: deltaTime, tangentRange: geometry.trackRange, depth: depth,
                          reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion)
                agent.trackPosition = geometry.clamped(drag.position.x)
                agent.target = agent.trackPosition
                agent.idleRemaining = 1
                agent.velocity = 0
                node.position = geometry.worldPoint(forDrag: drag.position)
                let rotationSign: CGFloat = (edge == .top || edge == .left) ? -1 : 1
                node.setDragRotation(drag.rotation * rotationSign)
                if let pinPoint = drag.pin(tangentRange: geometry.trackRange, depth: depth) {
                    let pinWorld = geometry.worldPoint(forDrag: pinPoint)
                    let bodyWorld = node.position
                    let dx = pinWorld.x - bodyWorld.x
                    let dy = pinWorld.y - bodyWorld.y
                    let distance = max(1, hypot(dx, dy))
                    let path = CGMutablePath()
                    path.move(to: pinWorld)
                    path.addLine(to: CGPoint(x: bodyWorld.x + dx / distance * min(16, distance),
                                             y: bodyWorld.y + dy / distance * min(16, distance)))
                    dragRope.path = path
                    dragRope.isHidden = false
                    dragPin.position = pinWorld
                    dragPin.isHidden = false
                }
                if drag.landed {
                    characterDrags.removeValue(forKey: agent.id)
                    if characterDrags.isEmpty { dragBridge?.onActivityChanged?(false) }
                } else { characterDrags[agent.id] = drag }
            } else {
                node.position = geometry.point(for: agent.trackPosition)
                node.setDragRotation(0)
            }
            agents[agent.id] = agent
            let moving = abs(agent.velocity) > 2 && agent.idleRemaining <= 0 && !stoppedIDs.contains(agent.id)
            node.updateMotion(member: member, moving: moving, velocity: agent.velocity, edge: edge)
            node.updatePresentationLayout(
                tangentPosition: agent.trackPosition,
                tangentLength: geometry.tangentLength,
                inwardNormalLength: bubbleInwardNormalLength,
                edge: edge
            )
        }
        updateProjectiles(currentTime: currentTime)
        if currentTime - lastHotspotReportTime >= 1.0 / 15.0 {
            lastHotspotReportTime = currentTime
            reportCharacterFrames(force: false)
        }
    }

    @discardableResult
    func toggleTreeMovement(for userID: UUID) -> Bool {
        guard members[userID]?.characterID == PixelCharacterCatalog.pixelTreeID else { return false }
        if stationaryTreeIDs.remove(userID) == nil {
            stationaryTreeIDs.insert(userID)
            agents[userID]?.velocity = 0
        } else {
            agents[userID]?.idleRemaining = 0
        }
        return true
    }

    func isTreeMovementPaused(for userID: UUID) -> Bool { stationaryTreeIDs.contains(userID) }

    var nodeIDs: Set<UUID> { Set(characterNodes.keys) }
    var agentStates: [PixelMovementAgent] { agents.values.sorted { $0.id.uuidString < $1.id.uuidString } }
    var trackGeometry: EdgeTrackGeometry {
        EdgeTrackGeometry(bounds: effectiveActivityFrame, edge: edge)
    }
    var messageBubbleTangentRanges: [UUID: ClosedRange<CGFloat>] {
        let geometry = trackGeometry
        return activeBubbles.reduce(into: [:]) { ranges, entry in
            guard let agent = agents[entry.key], members[entry.key] != nil else { return }
            let layouts = PixelBubbleStackLayout.make(
                bubbles: entry.value,
                tangentPosition: agent.trackPosition,
                tangentLength: geometry.tangentLength,
                edge: edge,
                inwardNormalLength: bubbleInwardNormalLength
            )
            ranges[entry.key] = PixelBubbleStackLayout.bodyTangentRange(
                for: layouts,
                at: agent.trackPosition,
                edge: edge
            )
        }
    }

    func renderedCharacterID(for memberID: UUID) -> String? {
        characterNodes[memberID]?.characterID
    }

    func renderedBubbleBody(for memberID: UUID) -> String? {
        characterNodes[memberID]?.bubbleBody
    }

    func renderedBubbleBodies(for memberID: UUID) -> [String] {
        characterNodes[memberID]?.bubbleBodies ?? []
    }

    func renderedBubbleTailFlags(for memberID: UUID) -> [Bool] {
        characterNodes[memberID]?.bubbleTailFlags ?? []
    }

    func renderedBubbleBodyFrames(for memberID: UUID) -> [CGRect] {
        characterNodes[memberID]?.bubbleBodyFrames ?? []
    }

    func renderedBubbleTotalFrames(for memberID: UUID) -> [CGRect] {
        characterNodes[memberID]?.bubbleTotalFrames ?? []
    }

    func renderedBubbleIsTyping(for memberID: UUID) -> Bool {
        characterNodes[memberID]?.bubbleIsTyping ?? false
    }

    func renderedNicknameWorldRotation(for memberID: UUID) -> CGFloat? {
        characterNodes[memberID]?.nicknameWorldRotation
    }

    func renderedBubbleTextWorldRotations(for memberID: UUID) -> [CGFloat] {
        characterNodes[memberID]?.bubbleTextWorldRotations ?? []
    }

    func renderedBubbleWorldRotations(for memberID: UUID) -> [CGFloat] {
        characterNodes[memberID]?.bubbleWorldRotations ?? []
    }

    func renderedVisualState(for memberID: UUID) -> PixelCharacterVisualState? {
        characterNodes[memberID]?.visualState
    }

    func renderedDozeText(for memberID: UUID) -> String? {
        characterNodes[memberID]?.dozeText
    }

    func hasRenderedDozeActions(for memberID: UUID) -> Bool {
        characterNodes[memberID]?.hasDozeActions ?? false
    }

    func renderedPulseCount(for memberID: UUID) -> Int {
        characterNodes[memberID]?.pulsePlayCount ?? 0
    }

    func hasRenderedAmbientSparkles(for memberID: UUID) -> Bool {
        characterNodes[memberID]?.hasAmbientSparkleAction ?? false
    }

    func renderedPulseSparkleCount(for memberID: UUID) -> Int {
        characterNodes[memberID]?.pulseSparkleSpawnCount ?? 0
    }

    var activeProjectileCount: Int { projectiles.count }
    var renderedProjectilePositions: [CGPoint] { projectiles.map { $0.node.position } }
    var renderedDragPinPosition: CGPoint? { dragPin.isHidden ? nil : dragPin.position }
    var renderedDragRopeBounds: CGRect? { dragRope.isHidden ? nil : dragRope.path?.boundingBoxOfPath }
    func renderedCharacterPosition(for memberID: UUID) -> CGPoint? { characterNodes[memberID]?.position }

    func renderedThrowCount(for memberID: UUID) -> Int {
        characterNodes[memberID]?.throwPlayCount ?? 0
    }

    func renderedHitCount(for memberID: UUID) -> Int {
        characterNodes[memberID]?.hitPlayCount ?? 0
    }

    func renderedPulsePeakScale(for memberID: UUID) -> CGFloat? {
        characterNodes[memberID]?.lastPulsePeakScale
    }

    func renderedBubbleDecorationCount(for memberID: UUID) -> Int {
        characterNodes[memberID]?.bubbleDecorationCount ?? 0
    }

    @discardableResult
    func playLocalPreviewPulse(
        memberID: UUID,
        at currentTime: TimeInterval = ProcessInfo.processInfo.systemUptime
    ) -> Bool {
        guard !stunState.isStunned(memberID, at: currentTime) else { return false }
        guard renderingConfiguration.allowsLocalPreviewEvents,
              let node = characterNodes[memberID],
              currentTime - (lastLocalPulseTimes[memberID] ?? -.infinity) >= 1
        else { return false }
        lastLocalPulseTimes[memberID] = currentTime
        node.playPulse(peakScale: renderingConfiguration.pulsePeakScale)
        recordPreviewEvent(.pulse(memberID))
        return true
    }

    @discardableResult
    func playLocalPreviewPulse(at point: CGPoint) -> Bool {
        guard let memberID = memberID(at: point) else { return false }
        return playLocalPreviewPulse(memberID: memberID)
    }

    func memberID(at point: CGPoint) -> UUID? {
        characterNodes
            .sorted(by: { $0.key.uuidString < $1.key.uuidString })
            .first(where: { _, node in
                CGRect(
                    x: node.position.x - EdgeTrackGeometry.hotspotPointSize / 2,
                    y: node.position.y - EdgeTrackGeometry.hotspotPointSize / 2,
                    width: EdgeTrackGeometry.hotspotPointSize,
                    height: EdgeTrackGeometry.hotspotPointSize
                ).contains(point)
            })?.key
    }

    func playLocalPreviewThrow(_ event: CharacterThrowEvent, playsSound: Bool = true) {
        guard renderingConfiguration.allowsLocalPreviewEvents else { return }
        guard !stunState.isStunned(event.actorUserID, at: clock()) else { return }
        beginThrow(event, playsSound: playsSound)
    }

    func cancelLocalPreviewPlayback() {
        guard renderingConfiguration.allowsLocalPreviewEvents else { return }
        resetStunState()
        projectiles.forEach { $0.node.removeFromParent() }
        projectiles.removeAll()
        removeImpactEffects()
        hitUntil.removeAll()
    }

    private var composerAvoidanceRects: [CGRect] {
        PixelWorldAvoidanceLayout.composerRects(
            activityFrame: effectiveActivityFrame,
            edge: edge,
            composerVisible: composerVisible,
            composerFrame: composerFrame
        )
    }

    private var effectiveActivityFrame: CGRect {
        guard let activityFrame,
              activityFrame.width > 0,
              activityFrame.height > 0
        else { return frame }
        return activityFrame
    }

    private var bubbleInwardNormalLength: CGFloat {
        let geometry = trackGeometry
        let anchor = geometry.point(for: geometry.trackRange.lowerBound)
        return switch edge {
        case .bottom: size.height - anchor.y
        case .top: anchor.y
        case .left: size.width - anchor.x
        case .right: anchor.x
        }
    }

    private func stableTrackPosition(roomID: UUID?, userID: UUID, salt: UInt64 = 0) -> CGFloat {
        let range = roamingTrackRange
        return range.lowerBound + (range.upperBound - range.lowerBound)
            * stableUnit(roomID: roomID, userID: userID, salt: salt)
    }

    private var roamingTrackRange: ClosedRange<CGFloat> {
        let length = trackGeometry.tangentLength
        let inset = min(renderingConfiguration.minimumTrackInset, max(0, length / 2))
        return inset...max(inset, length - inset)
    }

    private func initialTrackPosition(roomID: UUID?, userID: UUID) -> CGFloat {
        if let fraction = renderingConfiguration.fixedTrackFractions[userID] {
            return trackGeometry.clamped(trackGeometry.tangentLength * min(max(fraction, 0), 1))
        }
        if let fraction = renderingConfiguration.initialTrackFractions[userID] {
            return trackGeometry.clamped(trackGeometry.tangentLength * min(max(fraction, 0), 1))
        }
        return stableTrackPosition(roomID: roomID, userID: userID)
    }

    private func initialTarget(roomID: UUID?, userID: UUID) -> CGFloat {
        if renderingConfiguration.fixedTrackFractions[userID] != nil {
            return initialTrackPosition(roomID: roomID, userID: userID)
        }
        return stableTrackPosition(roomID: roomID, userID: userID, salt: 1)
    }

    private func configuredTrackPosition(_ position: CGFloat, userID: UUID) -> CGFloat {
        if renderingConfiguration.fixedTrackFractions[userID] != nil {
            return initialTrackPosition(roomID: currentRoomID, userID: userID)
        }
        let range = roamingTrackRange
        return min(max(position, range.lowerBound), range.upperBound)
    }

    private func recordPreviewEvent(_ event: PixelWorldRenderEvent) {
        guard renderingConfiguration.allowsLocalPreviewEvents else { return }
        previewRenderEvents.append(event)
        if previewRenderEvents.count > 64 {
            previewRenderEvents.removeFirst(previewRenderEvents.count - 64)
        }
    }

    private func randomTarget(for userID: UUID, time: TimeInterval) -> CGFloat {
        stableTrackPosition(
            roomID: currentRoomID,
            userID: userID,
            salt: UInt64(max(0, time * 10)) &+ 0xD1B54A32D192ED03
        )
    }

    private func stableUnit(roomID: UUID?, userID: UUID, salt: UInt64) -> CGFloat {
        var hash = installationSeed ^ salt ^ 0xcbf29ce484222325
        for byte in uuidBytes(roomID) + uuidBytes(userID) {
            hash ^= UInt64(byte)
            hash &*= 0x100000001b3
        }
        hash ^= hash >> 30
        hash &*= 0xbf58476d1ce4e5b9
        hash ^= hash >> 27
        hash &*= 0x94d049bb133111eb
        hash ^= hash >> 31
        return CGFloat(hash & 0x00FF_FFFF) / CGFloat(0x0100_0000)
    }

    private func uuidBytes(_ id: UUID?) -> [UInt8] {
        guard var value = id?.uuid else { return Array(repeating: 0, count: 16) }
        return withUnsafeBytes(of: &value) { Array($0) }
    }

    private func reportCharacterFrames(force: Bool) {
        // SwiftUI can also apply model updates between native layout passes.
        // Do not publish old local anchors under the new panel's screen origin.
        guard dragBridge?.renderView?.isViewportReady != false else { return }
        if let onChatBubbleFramesChanged {
            let chatBubbleFrames = characterNodes.reduce(into: [UUID: CGRect]()) { result, entry in
                result[entry.key] = entry.value.chatBubbleFrame(in: self)
            }
            if force || chatBubbleFrames != lastChatBubbleFrames {
                lastChatBubbleFrames = chatBubbleFrames
                onChatBubbleFramesChanged(chatBubbleFrames)
            }
        }
        let frames = characterNodes.reduce(into: [UUID: CGRect]()) { result, entry in
            result[entry.key] = CGRect(
                x: entry.value.position.x - EdgeTrackGeometry.hotspotPointSize / 2,
                y: entry.value.position.y - EdgeTrackGeometry.hotspotPointSize / 2,
                width: EdgeTrackGeometry.hotspotPointSize,
                height: EdgeTrackGeometry.hotspotPointSize
            )
        }
        let moved = frames.count != lastHotspotFrames.count || frames.contains { id, frame in
            guard let old = lastHotspotFrames[id] else { return true }
            return hypot(old.midX - frame.midX, old.midY - frame.midY) >= 1
        }
        guard force || moved else { return }
        lastHotspotFrames = frames
        onCharacterFramesChanged?(frames)
        let currentID = members.values.first(where: \.isCurrentUser)?.id
        onCurrentUserFrameChanged?(currentID.flatMap { frames[$0] })
    }

    private func beginThrow(_ event: CharacterThrowEvent, playsSound: Bool = true) {
        guard !recentThrowEventIDSet.contains(event.id),
              event.actorUserID != event.targetUserID,
              characterNodes[event.actorUserID] != nil,
              characterNodes[event.targetUserID] != nil
        else { return }
        recentThrowEventIDSet.insert(event.id)
        recentThrowEventIDs.append(event.id)
        if recentThrowEventIDs.count > 256 {
            recentThrowEventIDSet.remove(recentThrowEventIDs.removeFirst())
        }
        // Consume events received while locked so a later view update cannot replay them.
        guard suspendedReasons.isEmpty else { return }
        characterNodes[event.actorUserID]?.playThrow(sourceCharacterID: event.sourceCharacterID)
        recordPreviewEvent(.throwStarted(event.actorUserID))
        guard projectiles.count < PixelCharacterThrowStyle.maximumActiveProjectiles,
              let actor = characterNodes[event.actorUserID],
              let target = characterNodes[event.targetUserID]
        else { return }

        let textures = PixelCharacterThrowTextureStore.shared.textures(
            for: event.sourceCharacterID,
            throwableID: event.throwableID
        )
        if textures.usesCannonEmitter {
            actor.playCannonEmitter(
                frames: textures.emitterFrames,
                facingScale: emitterFacingScale(actor: actor.position, target: target.position)
            )
        }
        guard let first = textures.rotationFrames.first else { return }
        let projectileNode = SKSpriteNode(texture: first, size: CGSize(width: 32, height: 32))
        projectileNode.texture?.filteringMode = .nearest
        projectileNode.zPosition = 100
        projectileNode.isHidden = true
        addChild(projectileNode)
        let start = actor.position
        let distance = hypot(target.position.x - start.x, target.position.y - start.y)
        projectiles.append(ActiveCharacterProjectile(
            event: event,
            node: projectileNode,
            startedAt: clock(),
            flightDuration: PixelCharacterThrowStyle.flightDuration(for: distance),
            startPoint: start,
            inwardArcHeight: PixelCharacterThrowStyle.arcHeight(for: distance),
            playsSound: playsSound
        ))
    }

    private func updateProjectiles(currentTime: TimeInterval) {
        var survivors: [ActiveCharacterProjectile] = []
        for var projectile in projectiles {
            let elapsed = currentTime - projectile.startedAt
            if elapsed < PixelCharacterThrowStyle.releaseDelay {
                survivors.append(projectile)
                continue
            }
            if !projectile.hasReleased {
                projectile.hasReleased = true
                recordPreviewEvent(.projectileReleased(projectile.event.actorUserID))
            }
            guard let targetNode = characterNodes[projectile.event.targetUserID] else {
                projectile.node.removeFromParent()
                continue
            }
            let progress = min(1, max(0, CGFloat(
                (elapsed - PixelCharacterThrowStyle.releaseDelay) / projectile.flightDuration
            )))
            projectile.node.isHidden = false
            let end = targetNode.position
            let midpoint = CGPoint(x: (projectile.startPoint.x + end.x) / 2, y: (projectile.startPoint.y + end.y) / 2)
            let control: CGPoint = switch edge {
            case .bottom: CGPoint(x: midpoint.x, y: midpoint.y + projectile.inwardArcHeight)
            case .top: CGPoint(x: midpoint.x, y: midpoint.y - projectile.inwardArcHeight)
            case .left: CGPoint(x: midpoint.x + projectile.inwardArcHeight, y: midpoint.y)
            case .right: CGPoint(x: midpoint.x - projectile.inwardArcHeight, y: midpoint.y)
            }
            let inverse = 1 - progress
            projectile.node.position = CGPoint(
                x: inverse * inverse * projectile.startPoint.x + 2 * inverse * progress * control.x + progress * progress * end.x,
                y: inverse * inverse * projectile.startPoint.y + 2 * inverse * progress * control.y + progress * progress * end.y
            )
            let textures = PixelCharacterThrowTextureStore.shared.textures(
                for: projectile.event.sourceCharacterID,
                throwableID: projectile.event.throwableID
            ).rotationFrames
            let frame = Int(max(0, elapsed - PixelCharacterThrowStyle.releaseDelay) / PixelCharacterThrowStyle.rotationFrameInterval)
            projectile.node.texture = textures[frame % textures.count]
            if progress < 1 {
                survivors.append(projectile)
            } else {
                projectile.node.removeFromParent()
                playImpact(
                    at: characterTorsoPoint(from: end),
                    sourceCharacterID: projectile.event.sourceCharacterID,
                    throwableID: projectile.event.throwableID
                )
                recordPreviewEvent(.impact(projectile.event.targetUserID))
                if projectile.playsSound, elapsed <= PixelCharacterThrowStyle.releaseDelay + projectile.flightDuration + 0.5 {
                    onCharacterImpact?(PixelCharacterThrowCatalog.resolvedObjectID(
                        for: projectile.event.sourceCharacterID, equippedObjectID: projectile.event.throwableID), currentTime)
                }
                let targetID = projectile.event.targetUserID
                stunState.recordHit(targetID, at: currentTime)
                if stunState.isStunned(targetID, at: currentTime) {
                    hitUntil.removeValue(forKey: targetID)
                    targetNode.updateStun(startedAt: stunState.startedAt[targetID], time: currentTime)
                    if let member = members[targetID] {
                        targetNode.updateMotion(member: member, moving: false, velocity: 0, edge: edge)
                    }
                    continue
                }
                if let target = members[projectile.event.targetUserID] {
                    hitUntil[projectile.event.targetUserID] = currentTime + PixelCharacterThrowStyle.hitDuration
                    targetNode.playHit(sourceCharacterID: target.characterID)
                    recordPreviewEvent(.hit(projectile.event.targetUserID))
                }
            }
        }
        projectiles = survivors
    }

    private func removeImpactEffects() {
        children.filter { $0.name == "throwable-impact" }.forEach {
            $0.removeAllActions()
            $0.removeFromParent()
        }
    }

    private func playImpact(at point: CGPoint, sourceCharacterID: String, throwableID: String?) {
        let textures = PixelCharacterThrowTextureStore.shared.textures(
            for: sourceCharacterID,
            throwableID: throwableID
        )
        let frames = textures.impactFrames
        let durations = PixelCharacterThrowStyle.impactFrameDurations(for: textures.objectID)
        guard let first = frames.first else { return }
        let node = SKSpriteNode(
            texture: first,
            size: CGSize(
                width: PixelCharacterThrowStyle.impactPointSize,
                height: PixelCharacterThrowStyle.impactPointSize
            )
        )
        node.position = point
        node.name = "throwable-impact"
        node.zPosition = 101
        addChild(node)
        let poses = zip(frames, durations).map { texture, duration in
            SKAction.animate(with: [texture], timePerFrame: duration)
        }
        node.run(.sequence(poses + [.removeFromParent()]), withKey: "impact-frames")
    }

    private func characterTorsoPoint(from center: CGPoint) -> CGPoint {
        let offset = PixelCharacterThrowStyle.impactTorsoOffset
        return switch edge {
        case .bottom: CGPoint(x: center.x, y: center.y + offset)
        case .top: CGPoint(x: center.x, y: center.y - offset)
        case .left: CGPoint(x: center.x + offset, y: center.y)
        case .right: CGPoint(x: center.x - offset, y: center.y)
        }
    }

    private func emitterFacingScale(actor: CGPoint, target: CGPoint) -> CGFloat {
        let tangentDelta: CGFloat = switch edge {
        case .bottom: target.x - actor.x
        case .top: actor.x - target.x
        case .left: actor.y - target.y
        case .right: target.y - actor.y
        }
        return tangentDelta < 0 ? -1 : 1
    }
}

enum PixelNameplateLayout {
    static let verticalPosition: CGFloat = 32
    static let statusDotRadius: CGFloat = 3
    static let spacing: CGFloat = 5
    static let horizontalPadding: CGFloat = 4
    static let verticalPadding: CGFloat = 2
    static let cornerRadius: CGFloat = 6
    static let backgroundColor = NSColor(srgbRed: 0.02, green: 0.025, blue: 0.035, alpha: 0.62)

    static func statusDotPosition(nicknameFrame: CGRect) -> CGPoint {
        CGPoint(
            x: nicknameFrame.minX - spacing - statusDotRadius,
            y: nicknameFrame.midY
        )
    }

    static func backgroundFrame(nicknameFrame: CGRect) -> CGRect {
        nicknameFrame.insetBy(dx: -horizontalPadding, dy: -verticalPadding)
    }
}

private final class PixelCharacterNode: SKNode {
    private static let animationKey = "pixel-character-motion"
    private static let pulseAnimationKey = "pixel-character-pulse"
    private static let dozeMotionKey = "pixel-character-doze-motion"
    private static let cannonEmitterKey = "pixel-character-cannon-emitter"
    private let memberID: UUID
    private let presentation = SKNode()
    private let spritePulseAnchor = SKNode()
    private let sprite = SKSpriteNode()
    private let ambientSparkleLayer = SKNode()
    private let pulseSparkleLayer = SKNode()
    private let nameplateLayer = SKNode()
    private let nameplateBackground = SKShapeNode()
    private let nickname = SKLabelNode(fontNamed: "AppleSDGothicNeo-SemiBold")
    private let statusDot = SKShapeNode(circleOfRadius: PixelNameplateLayout.statusDotRadius)
    private let suppressedEye = SKShapeNode()
    var showsOverlaySuppressed: Bool { !suppressedEye.isHidden }
    func setDragRotation(_ rotation: CGFloat) { sprite.zRotation = rotation }

    private let dozeEffect = SKNode()
    private let dozeLabel = SKLabelNode(fontNamed: "AppleSDGothicNeo-Bold")
    private let foregroundEffectLayer = SKNode()
    private var messageBubbleNodes: [UUID: MessageBubbleNode] = [:]
    private var orderedMessageIDs: [UUID] = []
    private var messageBubbles: [ActiveBubble] = []
    private var typingBubbleNode: TypingIndicatorNode?
    private var currentMotion: PixelCharacterMotion?
    private var actionUntil: TimeInterval = 0
    private(set) var characterID: String
    private(set) var pulsePlayCount = 0
    private(set) var pulseSparkleSpawnCount = 0
    private(set) var throwPlayCount = 0
    private(set) var hitPlayCount = 0
    private(set) var lastPulsePeakScale: CGFloat?
    private var sparkleEffect: PixelSparkleEffect?
    private let stunEffect = PixelCharacterStunEffect()
    private var stunStartedAt: TimeInterval?
    private var stunElapsed: TimeInterval = 0
    var stunStarCount: Int { stunStartedAt == nil ? 0 : 3 }

    func updateStun(startedAt: TimeInterval?, time: TimeInterval) {
        let changed = stunStartedAt != startedAt
        stunStartedAt = startedAt
        stunEffect.isHidden = startedAt == nil
        if changed {
            currentMotion = nil
            actionUntil = 0
            sprite.removeAction(forKey: Self.animationKey)
        }
        guard let startedAt else { return }
        stunElapsed = max(0, time - startedAt)
        if changed {
            spritePulseAnchor.removeAction(forKey: Self.pulseAnimationKey)
            spritePulseAnchor.setScale(1)
            pulseSparkleLayer.removeAllActions()
            pulseSparkleLayer.removeAllChildren()
            setDozeVisible(false)
            stopAmbientSparkles()
        }
        stunEffect.update(elapsed: stunElapsed, reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion)
    }

    var bubbleBody: String? {
        orderedMessageIDs.last.flatMap { messageBubbleNodes[$0]?.body }
            ?? typingBubbleNode?.body
    }
    var bubbleBodies: [String] {
        orderedMessageIDs.compactMap { messageBubbleNodes[$0]?.body }
    }
    var bubbleTailFlags: [Bool] {
        orderedMessageIDs.compactMap { messageBubbleNodes[$0]?.includesTail }
    }
    var bubbleDecorationCount: Int {
        let messageCount = orderedMessageIDs.reduce(into: 0) { count, id in
            if messageBubbleNodes[id]?.hasDecoration == true { count += 1 }
        }
        return messageCount + (typingBubbleNode?.hasDecoration == true ? 1 : 0)
    }
    var bubbleBodyFrames: [CGRect] {
        orderedMessageIDs.compactMap { messageBubbleNodes[$0]?.bodyFrame }
    }
    var bubbleTotalFrames: [CGRect] {
        let messageFrames = orderedMessageIDs.compactMap {
            messageBubbleNodes[$0]?.totalFrame
        }
        let typingFrame = typingBubbleNode.map { [$0.totalFrame] } ?? []
        return messageFrames + typingFrame
    }
    func chatBubbleFrame(in scene: SKScene) -> CGRect? {
        let frames = bubbleTotalFrames
        guard !frames.isEmpty else { return nil }
        return frames.reduce(into: CGRect.null) { union, frame in
            let corners = [
                CGPoint(x: frame.minX, y: frame.minY),
                CGPoint(x: frame.maxX, y: frame.minY),
                CGPoint(x: frame.minX, y: frame.maxY),
                CGPoint(x: frame.maxX, y: frame.maxY)
            ]
            for corner in corners {
                let point = presentation.convert(corner, to: scene)
                union = union.union(CGRect(origin: point, size: .zero))
            }
        }
    }
    var bubbleIsTyping: Bool { typingBubbleNode != nil }
    var nicknameWorldRotation: CGFloat {
        presentation.zRotation + nameplateLayer.zRotation
    }
    var bubbleTextWorldRotations: [CGFloat] {
        let messageRotations = orderedMessageIDs.compactMap {
            messageBubbleNodes[$0]?.textCounterRotation
        }
        let typingRotation = typingBubbleNode.map { [$0.textCounterRotation] } ?? []
        return (messageRotations + typingRotation).map { presentation.zRotation + $0 }
    }
    var bubbleWorldRotations: [CGFloat] {
        let messageRotations = orderedMessageIDs.compactMap {
            messageBubbleNodes[$0]?.zRotation
        }
        let typingRotation = typingBubbleNode.map { [$0.zRotation] } ?? []
        return (messageRotations + typingRotation).map { presentation.zRotation + $0 }
    }
    var dozeText: String? { dozeLabel.text }
    var hasDozeActions: Bool {
        dozeEffect.action(forKey: Self.dozeMotionKey) != nil
    }
    var hasAmbientSparkleAction: Bool {
        ambientSparkleLayer.action(forKey: PixelSparkleStyle.ambientActionKey) != nil
    }
    var visualState: PixelCharacterVisualState? {
        currentMotion.map {
            PixelCharacterVisualState(
                motion: $0,
                alpha: sprite.alpha,
                colorBlendFactor: sprite.colorBlendFactor,
                showsDozeLabel: !dozeEffect.isHidden,
                facingScale: sprite.xScale
            )
        }
    }

    init(memberID: UUID, characterID: String) {
        self.memberID = memberID
        self.characterID = PixelCharacterCatalog.canonicalID(for: characterID)
        self.sparkleEffect = PixelCharacterCatalog.definition(for: characterID).sparkleEffect
        super.init()
        addChild(presentation)
        sprite.size = CGSize(width: 48, height: 48)
        sprite.texture = PixelCharacterTextureStore.shared.textures(for: self.characterID).idle[0]
        sprite.texture?.filteringMode = .nearest
        spritePulseAnchor.position = CGPoint(x: 0, y: -EdgeTrackGeometry.footInset)
        sprite.position = CGPoint(x: 0, y: EdgeTrackGeometry.footInset)
        presentation.addChild(spritePulseAnchor)
        presentation.addChild(stunEffect)
        spritePulseAnchor.addChild(sprite)
        ambientSparkleLayer.zPosition = 8
        pulseSparkleLayer.zPosition = 9
        foregroundEffectLayer.zPosition = PixelCharacterThrowStyle.cannonEmitterZPosition
        presentation.addChild(ambientSparkleLayer)
        presentation.addChild(pulseSparkleLayer)
        presentation.addChild(foregroundEffectLayer)
        nameplateLayer.position = CGPoint(x: 0, y: PixelNameplateLayout.verticalPosition)
        presentation.addChild(nameplateLayer)

        nameplateBackground.fillColor = PixelNameplateLayout.backgroundColor
        nameplateBackground.strokeColor = .clear
        nameplateBackground.zPosition = 11
        nameplateLayer.addChild(nameplateBackground)

        nickname.fontSize = 11
        nickname.fontColor = .white
        nickname.verticalAlignmentMode = .center
        nickname.horizontalAlignmentMode = .center
        nickname.position = .zero
        nickname.zPosition = 12
        nameplateLayer.addChild(nickname)

        statusDot.strokeColor = .clear
        statusDot.position = PixelNameplateLayout.statusDotPosition(nicknameFrame: nickname.frame)
        statusDot.zPosition = 12
        nameplateLayer.addChild(statusDot)
        let eyePath = CGMutablePath()
        eyePath.move(to: CGPoint(x: -6, y: 0))
        eyePath.addQuadCurve(to: CGPoint(x: 6, y: 0), control: CGPoint(x: 0, y: 7))
        eyePath.addQuadCurve(to: CGPoint(x: -6, y: 0), control: CGPoint(x: 0, y: -7))
        eyePath.addEllipse(in: CGRect(x: -1.5, y: -1.5, width: 3, height: 3))
        eyePath.move(to: CGPoint(x: -6, y: 5))
        eyePath.addLine(to: CGPoint(x: 6, y: -5))
        suppressedEye.path = eyePath
        suppressedEye.lineWidth = 1.5
        suppressedEye.strokeColor = .white
        suppressedEye.fillColor = .clear
        suppressedEye.zPosition = 12
        suppressedEye.isHidden = true
        nameplateLayer.addChild(suppressedEye)

        dozeEffect.position = CGPoint(x: 26, y: 20)
        dozeEffect.zPosition = 14
        dozeEffect.isHidden = true
        for offset in PixelDozeLabelStyle.outlineOffsets {
            let outline = SKLabelNode(fontNamed: "AppleSDGothicNeo-Bold")
            outline.text = PixelDozeLabelStyle.text
            outline.fontSize = PixelDozeLabelStyle.fontSize
            outline.fontColor = PixelDozeLabelStyle.outlineColor
            outline.position = offset
            outline.zPosition = 0
            dozeEffect.addChild(outline)
        }
        dozeLabel.text = PixelDozeLabelStyle.text
        dozeLabel.fontSize = PixelDozeLabelStyle.fontSize
        dozeLabel.fontColor = PixelDozeLabelStyle.fillColor
        dozeLabel.zPosition = 1
        dozeEffect.addChild(dozeLabel)
        presentation.addChild(dozeEffect)
    }

    required init?(coder aDecoder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func apply(
        member: PixelWorldMember,
        bubbles: [ActiveBubble],
        edge: OverlayEdge,
        tangentPosition: CGFloat,
        tangentLength: CGFloat,
        inwardNormalLength: CGFloat
    ) {
        let canonicalID = PixelCharacterCatalog.canonicalID(for: member.characterID)
        if canonicalID != characterID {
            characterID = canonicalID
            sparkleEffect = PixelCharacterCatalog.definition(for: canonicalID).sparkleEffect
            currentMotion = nil
            sprite.removeAction(forKey: Self.animationKey)
            sprite.xScale = 1
            stopAmbientSparkles()
        }
        presentation.zRotation = edge.presentationRotation
        nameplateLayer.zRotation = edge.readableContentCounterRotation
        dozeEffect.zRotation = edge.readableContentCounterRotation
        nickname.text = member.isCurrentUser
            ? L10n.format("pixel_world.nickname.current_user", member.nickname)
            : member.nickname
        suppressedEye.isHidden = !member.overlaySuppressed
        suppressedEye.position = CGPoint(x: nickname.frame.maxX + 10, y: nickname.frame.midY)
        var backgroundFrame = PixelNameplateLayout.backgroundFrame(nicknameFrame: nickname.frame)
        if member.overlaySuppressed { backgroundFrame.size.width += 18 }
        let backgroundPath = CGMutablePath()
        backgroundPath.addRoundedRect(
            in: backgroundFrame,
            cornerWidth: PixelNameplateLayout.cornerRadius,
            cornerHeight: PixelNameplateLayout.cornerRadius
        )
        nameplateBackground.path = backgroundPath
        statusDot.position = PixelNameplateLayout.statusDotPosition(nicknameFrame: nickname.frame)
        statusDot.fillColor = PresenceIndicatorTone.tone(for: member.presence).color
        updateBubbles(
            bubbles: bubbles,
            isTyping: bubbles.isEmpty && member.isTyping,
            typingBubbleStyleID: member.equippedBubbleStyleID,
            tangentPosition: tangentPosition,
            tangentLength: tangentLength,
            inwardNormalLength: inwardNormalLength,
            edge: edge
        )
        updateMotion(member: member, moving: false, velocity: 0, edge: edge)
    }

    func playPulse(peakScale: CGFloat = PixelCharacterPulseStyle.peakScale) {
        guard stunStartedAt == nil else { return }
        pulsePlayCount += 1
        lastPulsePeakScale = peakScale
        spritePulseAnchor.removeAction(forKey: Self.pulseAnimationKey)
        spritePulseAnchor.setScale(1)

        let grow = SKAction.scale(to: peakScale, duration: PixelCharacterPulseStyle.growDuration)
        grow.timingMode = .easeOut
        let settle = SKAction.scale(to: 1, duration: PixelCharacterPulseStyle.settleDuration)
        settle.timingMode = .easeInEaseOut
        spritePulseAnchor.run(.sequence([grow, settle]), withKey: Self.pulseAnimationKey)
        spawnPulseSparkles()
    }

    func playThrow(sourceCharacterID: String) {
        guard stunStartedAt == nil else { return }
        throwPlayCount += 1
        playActionFrames(
            PixelCharacterThrowTextureStore.shared.textures(for: sourceCharacterID).throwFrames,
            duration: PixelCharacterThrowStyle.throwDuration
        )
    }

    func playHit(sourceCharacterID: String) {
        guard stunStartedAt == nil else { return }
        hitPlayCount += 1
        playActionFrames(
            PixelCharacterThrowTextureStore.shared.textures(for: sourceCharacterID).hitFrames,
            duration: PixelCharacterThrowStyle.hitDuration
        )
    }

    func playCannonEmitter(frames: [SKTexture], facingScale: CGFloat) {
        guard let first = frames.first else { return }
        foregroundEffectLayer.removeAllActions()
        foregroundEffectLayer.removeAllChildren()
        let pointSize = PixelCharacterThrowStyle.cannonEmitterPointSize
        let cannon = SKSpriteNode(texture: first, size: CGSize(width: pointSize, height: pointSize))
        cannon.texture?.filteringMode = .nearest
        cannon.anchorPoint = CGPoint(x: 0.5, y: 0.5)
        cannon.position = CGPoint(
            x: PixelCharacterThrowStyle.cannonEmitterTangentOffset * facingScale,
            y: PixelCharacterThrowStyle.cannonEmitterNormalOffset
        )
        cannon.xScale = facingScale
        foregroundEffectLayer.addChild(cannon)
        foregroundEffectLayer.run(.sequence([
            .wait(forDuration: 0.01),
            .run {
                cannon.run(.animate(
                    with: frames,
                    timePerFrame: PixelCharacterThrowStyle.throwDuration / Double(frames.count)
                ))
            },
            .wait(forDuration: PixelCharacterThrowStyle.throwDuration),
            .run { [weak self] in self?.foregroundEffectLayer.removeAllChildren() }
        ]), withKey: Self.cannonEmitterKey)
    }

    private func playActionFrames(_ frames: [SKTexture], duration: TimeInterval) {
        guard !frames.isEmpty else { return }
        actionUntil = ProcessInfo.processInfo.systemUptime + duration
        sprite.removeAction(forKey: Self.animationKey)
        currentMotion = nil
        sprite.run(.sequence([
            .animate(with: frames, timePerFrame: duration / Double(frames.count)),
            .run { [weak self] in self?.currentMotion = nil }
        ]), withKey: Self.animationKey)
    }

    func updateMotion(
        member: PixelWorldMember,
        moving: Bool,
        velocity: CGFloat,
        edge: OverlayEdge
    ) {
        var requested: PixelCharacterMotion
        switch member.presence {
        case .away:
            requested = .doze
        case .offline:
            requested = .offline
        case .reconnecting:
            requested = .stopped
        case .online, .typing:
            requested = moving ? .walk : .idle
        }

        sprite.alpha = member.presence == .offline ? 0.75 : 1
        sprite.color = .systemGray
        sprite.colorBlendFactor = member.presence == .offline ? 0.58 : 0
        sprite.xScale = PixelCharacterFacingPolicy.scale(
            mirrorsToMovementDirection: PixelCharacterCatalog.definition(for: characterID)
                .mirrorsToMovementDirection,
            velocity: velocity,
            edge: edge,
            previousScale: sprite.xScale
        )
        if stunStartedAt != nil { requested = .offline }
        setDozeVisible(stunStartedAt == nil && member.presence == .away)
        setAmbientSparklesActive(stunStartedAt == nil && PixelSparkleVisibilityPolicy.showsAmbient(for: member.presence))
        if stunStartedAt != nil {
            // Use the approved two sleeping frames without changing Presence tint or labels.
            sprite.removeAction(forKey: Self.animationKey)
            currentMotion = .offline
            let frames = PixelCharacterTextureStore.shared.textures(for: characterID).offline
            let index = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion ? 0 : Int(stunElapsed / 1.2) % frames.count
            sprite.texture = frames[index]
            return
        }

        guard ProcessInfo.processInfo.systemUptime >= actionUntil else { return }
        guard requested != currentMotion else { return }
        currentMotion = requested
        sprite.removeAction(forKey: Self.animationKey)
        let set = PixelCharacterTextureStore.shared.textures(for: characterID)
        let textures: [SKTexture]
        let duration: TimeInterval
        switch requested {
        case .idle:
            textures = set.idle
            duration = 0.55
        case .walk:
            textures = set.walk
            duration = 0.16
        case .doze:
            textures = set.doze
            duration = 0.8
        case .offline:
            textures = set.offline
            duration = 1.2
        case .stopped:
            sprite.texture = set.idle[0]
            return
        }
        textures.forEach { $0.filteringMode = .nearest }
        sprite.run(.repeatForever(.animate(with: textures, timePerFrame: duration)), withKey: Self.animationKey)
    }

    func updatePresentationLayout(
        tangentPosition: CGFloat,
        tangentLength: CGFloat,
        inwardNormalLength: CGFloat,
        edge: OverlayEdge
    ) {
        if messageBubbles.isEmpty {
            guard let typingBubbleNode else { return }
            typingBubbleNode.apply(layout: PixelBubbleLayout.make(
                text: typingBubbleNode.body,
                isTyping: true,
                tangentPosition: tangentPosition,
                tangentLength: tangentLength,
                edge: edge,
                leadingDecorationOverflow: typingBubbleNode.hasDecoration
                    ? PixelBubbleStyle.decorationLeadingOverflow
                    : 0,
                maximumContentWidth: edge.isHorizontal
                    ? nil
                    : max(
                        24,
                        inwardNormalLength - 56
                            - (edge == .right && typingBubbleNode.hasDecoration
                                ? PixelBubbleStyle.decorationLeadingOverflow
                                : 0)
                    )
            ))
            return
        }

        applyMessageLayouts(
            tangentPosition: tangentPosition,
            tangentLength: tangentLength,
            inwardNormalLength: inwardNormalLength,
            edge: edge
        )
    }

    private func updateBubbles(
        bubbles: [ActiveBubble],
        isTyping: Bool,
        typingBubbleStyleID: String?,
        tangentPosition: CGFloat,
        tangentLength: CGFloat,
        inwardNormalLength: CGFloat,
        edge: OverlayEdge
    ) {
        messageBubbles = Array(bubbles.suffix(ActiveBubbleLedger.maximumVisiblePerSender))
        guard !messageBubbles.isEmpty else {
            messageBubbleNodes.values.forEach { $0.removeFromParent() }
            messageBubbleNodes.removeAll()
            orderedMessageIDs.removeAll()

            guard isTyping else {
                typingBubbleNode?.removeFromParent()
                typingBubbleNode = nil
                return
            }

            let requestedTheme = PixelBubbleTheme.resolve(typingBubbleStyleID)
            let layout = PixelBubbleLayout.make(
                text: ".",
                isTyping: true,
                tangentPosition: tangentPosition,
                tangentLength: tangentLength,
                edge: edge,
                leadingDecorationOverflow: requestedTheme.decorationAssetName == nil
                    ? 0
                    : PixelBubbleStyle.decorationLeadingOverflow,
                maximumContentWidth: edge.isHorizontal
                    ? nil
                    : max(
                        24,
                        inwardNormalLength - 56
                            - (edge == .right && requestedTheme.decorationAssetName != nil
                                ? PixelBubbleStyle.decorationLeadingOverflow
                                : 0)
                    )
            )
            let requestedThemeID = requestedTheme.id
            if let typingBubbleNode, typingBubbleNode.theme.id == requestedThemeID {
                typingBubbleNode.apply(layout: layout)
            } else {
                typingBubbleNode?.removeFromParent()
                let node = TypingIndicatorNode(layout: layout, bubbleStyleID: typingBubbleStyleID)
                presentation.addChild(node)
                typingBubbleNode = node
            }
            return
        }

        typingBubbleNode?.removeFromParent()
        typingBubbleNode = nil
        let requestedIDs = Set(messageBubbles.map(\.messageID))
        for id in Array(messageBubbleNodes.keys) where !requestedIDs.contains(id) {
            messageBubbleNodes.removeValue(forKey: id)?.removeFromParent()
        }
        applyMessageLayouts(
            tangentPosition: tangentPosition,
            tangentLength: tangentLength,
            inwardNormalLength: inwardNormalLength,
            edge: edge
        )
    }

    private func applyMessageLayouts(
        tangentPosition: CGFloat,
        tangentLength: CGFloat,
        inwardNormalLength: CGFloat,
        edge: OverlayEdge
    ) {
        let entries = PixelBubbleStackLayout.make(
            bubbles: messageBubbles,
            tangentPosition: tangentPosition,
            tangentLength: tangentLength,
            edge: edge,
            inwardNormalLength: inwardNormalLength
        )
        orderedMessageIDs = entries.map { $0.bubble.messageID }
        for entry in entries {
            if let node = messageBubbleNodes[entry.bubble.messageID] {
                node.apply(layout: entry.layout, includesTail: entry.includesTail)
            } else {
                let node = MessageBubbleNode(
                    body: entry.bubble.body,
                    layout: entry.layout,
                    includesTail: entry.includesTail,
                    bubbleStyleID: entry.bubble.bubbleStyleID
                )
                presentation.addChild(node)
                messageBubbleNodes[entry.bubble.messageID] = node
            }
        }
    }

    private func setDozeVisible(_ visible: Bool) {
        guard dozeEffect.isHidden == visible else { return }
        dozeEffect.isHidden = !visible
        dozeEffect.removeAllActions()
        dozeEffect.position = CGPoint(x: 26, y: 20)
        guard visible else { return }
        dozeEffect.alpha = PixelDozeLabelStyle.restingAlpha
        dozeEffect.run(.repeatForever(.sequence([
            .group([
                .fadeAlpha(to: 1, duration: 0.5),
                .moveBy(x: 0, y: PixelDozeLabelStyle.floatingDistance, duration: 0.5)
            ]),
            .group([
                .fadeAlpha(to: PixelDozeLabelStyle.restingAlpha, duration: 0.5),
                .moveBy(x: 0, y: -PixelDozeLabelStyle.floatingDistance, duration: 0.5)
            ])
        ])), withKey: Self.dozeMotionKey)
    }

    private func setAmbientSparklesActive(_ active: Bool) {
        guard active, let effect = sparkleEffect else {
            stopAmbientSparkles()
            return
        }
        guard ambientSparkleLayer.action(forKey: PixelSparkleStyle.ambientActionKey) == nil else {
            return
        }
        let midpoint = (effect.ambientDelay.lowerBound + effect.ambientDelay.upperBound) / 2
        let range = effect.ambientDelay.upperBound - effect.ambientDelay.lowerBound
        ambientSparkleLayer.run(.repeatForever(.sequence([
            .wait(forDuration: midpoint, withRange: range),
            .run { [weak self] in self?.spawnAmbientSparkles() }
        ])), withKey: PixelSparkleStyle.ambientActionKey)
    }

    private func stopAmbientSparkles() {
        ambientSparkleLayer.removeAction(forKey: PixelSparkleStyle.ambientActionKey)
        ambientSparkleLayer.removeAllChildren()
    }

    private func spawnAmbientSparkles() {
        guard let effect = sparkleEffect else { return }
        let count = Int.random(in: effect.ambientCount)
        for index in 0..<count {
            let color = effect.colors[(index + Int.random(in: 0..<effect.colors.count)) % effect.colors.count]
            let radius = CGFloat.random(in: effect.ambientRadius)
            let star = makeStar(color: color, radius: radius)
            let targetScale = radius / 3
            star.setScale(targetScale * 0.32)
            star.position = CGPoint(
                x: CGFloat.random(in: effect.ambientHorizontalPosition),
                y: CGFloat.random(in: effect.ambientVerticalPosition)
            )
            ambientSparkleLayer.addChild(star)
            let half = effect.ambientDuration / 2
            star.run(.sequence([
                .group([
                    .fadeIn(withDuration: half),
                    .scale(to: targetScale, duration: half),
                    .moveBy(x: 0, y: effect.ambientRise / 2, duration: half)
                ]),
                .group([
                    .fadeOut(withDuration: half),
                    .scale(to: targetScale * 0.35, duration: half),
                    .moveBy(x: 0, y: effect.ambientRise / 2, duration: half)
                ]),
                .removeFromParent()
            ]))
        }
    }

    private func spawnPulseSparkles() {
        guard let effect = sparkleEffect else { return }
        pulseSparkleSpawnCount += effect.pulseCount
        pulseSparkleLayer.removeAction(forKey: PixelSparkleStyle.pulseActionKey)
        pulseSparkleLayer.removeAllChildren()
        spawnCentralFlash(effect: effect)
        var colorOffset = 0
        for (waveIndex, wave) in effect.pulseWaves.enumerated() {
            for index in 0..<wave.count {
                let angleOffset = waveIndex.isMultiple(of: 2)
                    ? CGFloat.zero
                    : .pi / CGFloat(max(1, wave.count))
                let angle = (CGFloat(index) / CGFloat(wave.count)) * .pi * 2 + angleOffset
                let progress = wave.count > 1 ? CGFloat(index) / CGFloat(wave.count - 1) : 0.5
                let distance = interpolated(in: wave.distance, progress: progress)
                let radius = index.isMultiple(of: 3)
                    ? wave.radius.upperBound
                    : interpolated(in: wave.radius, progress: 1 - progress)
                let star = makeStar(
                    color: effect.colors[(colorOffset + index) % effect.colors.count],
                    radius: radius
                )
                star.alpha = 0
                star.position = CGPoint(x: 0, y: EdgeTrackGeometry.footInset)
                pulseSparkleLayer.addChild(star)
                let move = SKAction.moveBy(
                    x: cos(angle) * distance,
                    y: sin(angle) * distance,
                    duration: wave.duration
                )
                move.timingMode = .easeOut
                let shrink = SKAction.scale(to: max(0.18, radius / 9), duration: wave.duration)
                shrink.timingMode = .easeIn
                star.run(.sequence([
                    .wait(forDuration: wave.delay),
                    .run { star.alpha = 1 },
                    .group([
                        move,
                        .fadeOut(withDuration: wave.duration),
                        .rotate(byAngle: .pi, duration: wave.duration),
                        shrink
                    ]),
                    .removeFromParent()
                ]))
            }
            colorOffset += wave.count
        }
        pulseSparkleLayer.run(
            .wait(forDuration: effect.pulseDuration),
            withKey: PixelSparkleStyle.pulseActionKey
        )
    }

    private func spawnCentralFlash(effect: PixelSparkleEffect) {
        guard let gold = effect.colors.last else { return }
        let flash = makeStar(color: gold, radius: effect.centralFlashRadius)
        let targetScale = effect.centralFlashRadius / 3
        flash.alpha = 1
        flash.setScale(targetScale * 0.12)
        flash.position = CGPoint(x: 0, y: EdgeTrackGeometry.footInset)
        pulseSparkleLayer.addChild(flash)
        let half = effect.centralFlashDuration / 2
        let expand = SKAction.scale(to: targetScale, duration: half)
        expand.timingMode = .easeOut
        let settle = SKAction.scale(to: targetScale * 0.36, duration: half)
        settle.timingMode = .easeIn
        flash.run(.sequence([
            .group([expand, .fadeAlpha(to: 0.88, duration: half)]),
            .group([settle, .fadeOut(withDuration: half)]),
            .removeFromParent()
        ]))
    }

    private func interpolated(in range: ClosedRange<CGFloat>, progress: CGFloat) -> CGFloat {
        range.lowerBound + (range.upperBound - range.lowerBound) * progress
    }

    private func makeStar(color: PixelSparkleColor, radius: CGFloat) -> SKShapeNode {
        let star = SKShapeNode(path: PixelSparkleStyle.starPath())
        star.fillColor = NSColor(cgColor: color.cgColor) ?? .white
        star.strokeColor = .clear
        star.setScale(radius / 3)
        star.alpha = 0
        return star
    }
}

enum PixelCharacterMotion {
    case idle
    case walk
    case doze
    case offline
    case stopped
}

enum PixelBubbleStyle {
    static let backgroundColor = NSColor(srgbRed: 1, green: 1, blue: 1, alpha: 0.96)
    static let textColor = NSColor(srgbRed: 0.11, green: 0.12, blue: 0.16, alpha: 1)
    static let borderColor = NSColor(srgbRed: 0.08, green: 0.09, blue: 0.12, alpha: 0.16)
    static let decorationSize: CGFloat = 16
    static let decorationCornerInset: CGFloat = 2
    static let decorationLeadingOverflow = decorationSize / 2 - decorationCornerInset
}

struct PixelBubbleTheme {
    let id: String?
    let backgroundColor: NSColor
    let textColor: NSColor
    let decorationAssetName: String?

    static func resolve(_ id: String?) -> Self {
        switch id {
        case "bubble_bunny_pink":
            Self(
                id: id,
                backgroundColor: NSColor(srgbRed: 0xF7 / 255, green: 0xA9 / 255, blue: 0xB8 / 255, alpha: 0.96),
                textColor: NSColor(srgbRed: 0x1C / 255, green: 0x1F / 255, blue: 0x29 / 255, alpha: 1),
                decorationAssetName: id
            )
        case "bubble_butter_chick":
            Self(
                id: id,
                backgroundColor: NSColor(srgbRed: 0xFF / 255, green: 0xE3 / 255, blue: 0x8A / 255, alpha: 0.96),
                textColor: NSColor(srgbRed: 0x1C / 255, green: 0x1F / 255, blue: 0x29 / 255, alpha: 1),
                decorationAssetName: id
            )
        case "bubble_starry_cat":
            Self(
                id: id,
                backgroundColor: NSColor(srgbRed: 0x40 / 255, green: 0x3A / 255, blue: 0x78 / 255, alpha: 0.96),
                textColor: NSColor(srgbRed: 0xFF / 255, green: 0xF7 / 255, blue: 0xE8 / 255, alpha: 1),
                decorationAssetName: id
            )
        default:
            Self(
                id: nil,
                backgroundColor: PixelBubbleStyle.backgroundColor,
                textColor: PixelBubbleStyle.textColor,
                decorationAssetName: nil
            )
        }
    }
}

class PixelBubbleNode: SKNode {
    let body: String
    let isTyping: Bool
    private(set) var includesTail: Bool
    private(set) var bodyFrame: CGRect = .zero
    fileprivate let background = SKShapeNode()
    fileprivate let label = SKLabelNode(fontNamed: "AppleSDGothicNeo-Medium")
    private let decoration: SKSpriteNode?
    let theme: PixelBubbleTheme
    private var lastLayout: PixelBubbleLayout?

    init(
        body: String,
        isTyping: Bool,
        layout: PixelBubbleLayout,
        includesTail: Bool = true,
        bubbleStyleID: String? = nil
    ) {
        self.body = body
        self.isTyping = isTyping
        self.includesTail = includesTail
        self.theme = PixelBubbleTheme.resolve(bubbleStyleID)
        if let assetName = theme.decorationAssetName,
           let url = Bundle.main.url(
               forResource: assetName,
               withExtension: "png",
               subdirectory: "Bubbles"
           ) ?? Bundle.main.url(forResource: assetName, withExtension: "png"),
           let image = NSImage(contentsOf: url) {
            let texture = SKTexture(image: image)
            texture.filteringMode = .nearest
            self.decoration = SKSpriteNode(
                texture: texture,
                size: CGSize(width: PixelBubbleStyle.decorationSize, height: PixelBubbleStyle.decorationSize)
            )
        } else {
            self.decoration = nil
        }
        super.init()
        zPosition = 20
        background.fillColor = theme.backgroundColor
        background.strokeColor = PixelBubbleStyle.borderColor
        background.lineWidth = 1
        addChild(background)
        if let decoration {
            decoration.zPosition = 1
            addChild(decoration)
        }

        label.text = body
        label.fontSize = isTyping ? 16 : 10.5
        // SpriteKit resolves dynamic AppKit colors against the scene's dark
        // appearance, not against this white bubble. Use an explicit ink color
        // so Korean text stays readable in both system appearances.
        label.fontColor = theme.textColor
        label.zPosition = 2
        label.numberOfLines = 0
        label.lineBreakMode = .byCharWrapping
        label.verticalAlignmentMode = .center
        label.horizontalAlignmentMode = .center
        addChild(label)
        apply(layout: layout, includesTail: includesTail)
    }

    required init?(coder aDecoder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    var hasDecoration: Bool { decoration != nil }
    var decorationFrame: CGRect? { decoration?.frame }
    var totalFrame: CGRect { lastLayout?.totalFrame ?? .zero }
    var textCounterRotation: CGFloat { zRotation + label.zRotation }

    func apply(layout: PixelBubbleLayout) {
        apply(layout: layout, includesTail: includesTail)
    }

    func apply(layout: PixelBubbleLayout, includesTail: Bool) {
        guard layout != lastLayout || self.includesTail != includesTail else { return }
        lastLayout = layout
        self.includesTail = includesTail
        bodyFrame = layout.bodyFrame
        position = layout.nodePosition
        zRotation = layout.nodeRotation
        label.zRotation = layout.labelRotation
        label.preferredMaxLayoutWidth = max(8, layout.size.width - 16)
        label.position.x = 0
        let rect = CGRect(
            x: -layout.size.width / 2,
            y: -layout.size.height / 2,
            width: layout.size.width,
            height: layout.size.height
        )
        let path = CGMutablePath()
        path.addRoundedRect(in: rect, cornerWidth: 9, cornerHeight: 9)
        if includesTail {
            let halfBase: CGFloat = 6
            switch layout.tailAttachment {
            case .bottom:
                let baseCenter = min(max(layout.tailTip.x, rect.minX + 10), rect.maxX - 10)
                path.move(to: CGPoint(x: baseCenter - halfBase, y: rect.minY + 1))
                path.addLine(to: layout.tailTip)
                path.addLine(to: CGPoint(x: baseCenter + halfBase, y: rect.minY + 1))
            case .top:
                let baseCenter = min(max(layout.tailTip.x, rect.minX + 10), rect.maxX - 10)
                path.move(to: CGPoint(x: baseCenter + halfBase, y: rect.maxY - 1))
                path.addLine(to: layout.tailTip)
                path.addLine(to: CGPoint(x: baseCenter - halfBase, y: rect.maxY - 1))
            case .left:
                let baseCenter = min(max(layout.tailTip.y, rect.minY + 10), rect.maxY - 10)
                path.move(to: CGPoint(x: rect.minX + 1, y: baseCenter - halfBase))
                path.addLine(to: layout.tailTip)
                path.addLine(to: CGPoint(x: rect.minX + 1, y: baseCenter + halfBase))
            case .right:
                let baseCenter = min(max(layout.tailTip.y, rect.minY + 10), rect.maxY - 10)
                path.move(to: CGPoint(x: rect.maxX - 1, y: baseCenter + halfBase))
                path.addLine(to: layout.tailTip)
                path.addLine(to: CGPoint(x: rect.maxX - 1, y: baseCenter - halfBase))
            }
            path.closeSubpath()
        }
        background.path = path
        decoration?.position = CGPoint(
            x: rect.minX + PixelBubbleStyle.decorationCornerInset,
            y: rect.maxY
        )
    }
}

private final class MessageBubbleNode: PixelBubbleNode {
    init(body: String, layout: PixelBubbleLayout, includesTail: Bool = true, bubbleStyleID: String? = nil) {
        super.init(
            body: body,
            isTyping: false,
            layout: layout,
            includesTail: includesTail,
            bubbleStyleID: bubbleStyleID
        )
        label.text = body
    }

    required init?(coder aDecoder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }
}

final class TypingIndicatorNode: PixelBubbleNode {
    static let sequenceFrames = [".", "..", "..."]
    static let frameInterval: TimeInterval = 0.35

    init(layout: PixelBubbleLayout, bubbleStyleID: String? = nil) {
        super.init(body: ".", isTyping: true, layout: layout, bubbleStyleID: bubbleStyleID)
        let sequence = SKAction.sequence([
            .run { [weak self] in self?.label.text = Self.sequenceFrames[0] }, .wait(forDuration: Self.frameInterval),
            .run { [weak self] in self?.label.text = Self.sequenceFrames[1] }, .wait(forDuration: Self.frameInterval),
            .run { [weak self] in self?.label.text = Self.sequenceFrames[2] }, .wait(forDuration: Self.frameInterval)
        ])
        run(.repeatForever(sequence), withKey: "typing-dots")
    }

    required init?(coder aDecoder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }
}

struct PixelCharacterTextures {
    let idle: [SKTexture]
    let walk: [SKTexture]
    let doze: [SKTexture]
    let offline: [SKTexture]
}

@MainActor
final class PixelCharacterTextureStore {
    static let shared = PixelCharacterTextureStore()
    private var cache: [String: PixelCharacterTextures] = [:]

    func textures(for storedID: String, bundle: Bundle = .main) -> PixelCharacterTextures {
        let definition = PixelCharacterCatalog.definition(for: storedID)
        if let cached = cache[definition.id] { return cached }
        let sheet: SKTexture
        if let url = definition.assetURL(bundle: bundle), let image = NSImage(contentsOf: url) {
            sheet = SKTexture(image: image)
        } else {
            sheet = SKTexture(image: fallbackImage())
        }
        sheet.filteringMode = .nearest
        let frames = (0..<PixelCharacterCatalog.frameCount).map { index -> SKTexture in
            let texture = SKTexture(
                rect: CGRect(
                    x: CGFloat(index) / CGFloat(PixelCharacterCatalog.frameCount),
                    y: 0,
                    width: 1 / CGFloat(PixelCharacterCatalog.frameCount),
                    height: 1
                ),
                in: sheet
            )
            texture.filteringMode = .nearest
            return texture
        }
        let contract = definition.frames
        let result = PixelCharacterTextures(
            idle: Array(frames[contract.idle]),
            walk: Array(frames[contract.walk]),
            doze: Array(frames[contract.doze]),
            offline: Array(frames[contract.offline])
        )
        cache[definition.id] = result
        return result
    }

    private func fallbackImage() -> NSImage {
        let image = NSImage(size: PixelCharacterCatalog.sheetPixelSize)
        image.lockFocus()
        NSColor.systemOrange.setFill()
        for frame in 0..<PixelCharacterCatalog.frameCount {
            NSBezierPath(rect: CGRect(x: CGFloat(frame * 24 + 4), y: 3, width: 16, height: 17)).fill()
        }
        image.unlockFocus()
        return image
    }
}
