import AppKit
import Observation
import SpriteKit
import SwiftUI

private final class DrawResultPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

private final class DrawScreenObserverToken: @unchecked Sendable {
    let value: NSObjectProtocol
    init(_ value: NSObjectProtocol) { self.value = value }
    deinit { NotificationCenter.default.removeObserver(value) }
}

@MainActor @Observable
private final class DrawPlaybackState {
    enum Phase { case ready, playing, complete }
    var phase: Phase = .ready
    var progress: CGFloat = 0
}

@MainActor
final class DrawResultWindowController {
    private let stagePanel: DrawResultPanel
    private let controlsPanel: DrawResultPanel
    private var screenIdentifier: String?
    private var screenObserver: DrawScreenObserverToken?
    private var playbackTask: Task<Void, Never>?
    private var playback = DrawPlaybackState()
    private var event: DrawEvent?
    private var roomID: UUID?
    private var followsActiveRoom = true

    init() {
        stagePanel = DrawResultPanel(
            contentRect: CGRect(x: 0, y: 0, width: 820, height: 590),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false
        )
        controlsPanel = DrawResultPanel(
            contentRect: CGRect(x: 0, y: 0, width: 320, height: 64),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false
        )
        for panel in [stagePanel, controlsPanel] {
            panel.backgroundColor = .clear
            panel.isOpaque = false
            panel.hasShadow = false
            panel.level = .floating
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            panel.hidesOnDeactivate = false
            panel.canHide = false
            panel.isReleasedWhenClosed = false
        }
        // The game surface is visual only. Empty pixels pass pointer input to the app below.
        stagePanel.ignoresMouseEvents = true
        screenObserver = DrawScreenObserverToken(NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification,
            object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.reposition() }
        })
    }

    func show(event: DrawEvent, room: Room, screenIdentifier: String?, followsActiveRoom: Bool = true) {
        close()
        self.event = event
        self.followsActiveRoom = followsActiveRoom
        roomID = room.id
        self.screenIdentifier = screenIdentifier
        playback = DrawPlaybackState()
        stagePanel.contentView = NSHostingView(rootView: DrawGameStageView(
            event: event, room: room, playback: playback
        ))
        controlsPanel.contentView = NSHostingView(rootView: DrawGameControlsView(
            event: event, playback: playback,
            onStart: { [weak self] in self?.start() },
            onClose: { [weak self] in self?.close() }
        ))
        reposition()
        stagePanel.orderFrontRegardless()
        controlsPanel.makeKeyAndOrderFront(nil)
    }

    func close() {
        playbackTask?.cancel()
        playbackTask = nil
        stagePanel.orderOut(nil)
        controlsPanel.orderOut(nil)
        stagePanel.contentView = nil
        controlsPanel.contentView = nil
        event = nil
        roomID = nil
        followsActiveRoom = true
    }

    func closeIfRoomChanged(to roomID: UUID?) {
        if followsActiveRoom, let current = self.roomID, current != roomID { close() }
    }

    func closeIfRoomUnavailable(in roomIDs: Set<UUID>) {
        if let roomID, !roomIDs.contains(roomID) { close() }
    }

    func updateScreen(_ identifier: String?) {
        screenIdentifier = identifier
        if stagePanel.isVisible { reposition() }
    }

    private func start() {
        guard let event, playback.phase != .playing else { return }
        playbackTask?.cancel()
        playback.progress = 0
        playback.phase = .playing
        reposition()
        let reducedMotion = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        let duration: TimeInterval
        if reducedMotion {
            duration = 0.16
        } else {
            duration = switch event.kind {
            case .lot: 4.0
            case .ladder: Double(event.gameEntryCount) * 2.2
                + Double(max(0, event.gameEntryCount - 1)) * 0.2
            case .roulette: 5.0
            }
        }
        let started = ProcessInfo.processInfo.systemUptime
        playbackTask = Task { [weak self] in
            guard let self else { return }
            while !Task.isCancelled {
                let elapsed = ProcessInfo.processInfo.systemUptime - started
                let fraction = min(1, max(0, elapsed / duration))
                playback.progress = reducedMotion && fraction < 1 ? 0 : fraction
                if fraction >= 1 {
                    playback.phase = .complete
                    reposition()
                    playbackTask = nil
                    return
                }
                do { try await Task.sleep(for: .milliseconds(33)) } catch { return }
            }
        }
    }

    private func reposition() {
        let screen = NSScreen.screens.first(where: {
            $0.sideyOverlayGeometry.identifier == screenIdentifier
        }) ?? NSScreen.main ?? NSScreen.screens.first
        guard let frame = screen?.visibleFrame else { return }
        let width = min(920, max(320, frame.width - 32))
        let height = min(680, max(280, frame.height - 112))
        let stage = CGRect(x: frame.midX - width / 2,
                           y: frame.midY - height / 2 + 24,
                           width: width, height: height)
        if stagePanel.frame != stage { stagePanel.setFrame(stage, display: true) }
        let drawingBottom: CGFloat
        switch event?.kind {
        case .lot:
            drawingBottom = DrawLotScene.controlsAnchorY(
                entryCount: event?.gameEntryCount ?? 2, size: stage.size,
                completed: playback.phase == .complete
            )
        case .roulette:
            let radius = max(32, min(width * 0.37, height * 0.36))
            drawingBottom = height / 2 - 7 - radius - 3
        case .ladder:
            drawingBottom = DrawLadderScene.controlsAnchorY
        case nil:
            drawingBottom = 0
        }
        let controls = CGRect(x: frame.midX - 160,
                              y: max(frame.minY + 8, stage.minY + drawingBottom - 12 - 64),
                              width: 320, height: 64)
        if controlsPanel.frame != controls { controlsPanel.setFrame(controls, display: true) }
    }
}

private struct DrawGameControlsView: View {
    let event: DrawEvent
    @Bindable var playback: DrawPlaybackState
    let onStart: () -> Void
    let onClose: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Button(action: onStart) {
                Label(playback.phase == .complete ? L10n.text("draw.replay") : L10n.text("draw.start"),
                      systemImage: playback.phase == .complete ? "arrow.clockwise" : "play.fill")
            }
            .disabled(playback.phase == .playing)
            .buttonStyle(.borderedProminent)
            Button(action: onClose) {
                Label(L10n.text("draw.close_result"), systemImage: "xmark")
            }
            .buttonStyle(.bordered)
        }
        .padding(12)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 15))
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(event.kind.title)
        .accessibilityValue(
            playback.phase == .playing ? L10n.text("draw.animating") :
                playback.phase == .complete ? L10n.text("draw.result.title") : event.kind.title
        )
    }
}

private struct DrawGameStageView: NSViewRepresentable {
    let event: DrawEvent
    let room: Room
    @Bindable var playback: DrawPlaybackState

    func makeNSView(context: Context) -> SKView {
        let view = SKView(frame: .zero)
        PixelWorldRendererPolicy.apply(to: view)
        view.presentScene(makeScene(size: view.bounds.size))
        return view
    }

    func updateNSView(_ view: SKView, context: Context) {
        let progress = playback.progress
        switch view.scene {
        case let scene as DrawLadderScene: scene.setProgress(progress)
        case let scene as DrawLotScene: scene.setProgress(progress)
        case let scene as DrawRouletteScene: scene.setProgress(progress)
        default: break
        }
    }

    static func dismantleNSView(_ view: SKView, coordinator: Void) {
        view.isPaused = true
        view.presentScene(nil)
    }

    private func makeScene(size: CGSize) -> SKScene {
        let scene: SKScene = switch event.kind {
        case .ladder: DrawLadderScene(event: event, room: room, size: size)
        case .lot: DrawLotScene(event: event, room: room, size: size)
        case .roulette: DrawRouletteScene(event: event, room: room, size: size)
        }
        scene.scaleMode = .resizeFill
        scene.backgroundColor = .clear
        return scene
    }
}
