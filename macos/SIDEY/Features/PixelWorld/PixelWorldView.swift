import SpriteKit
import SwiftUI

struct PixelWorldView: View {
    @Bindable var model: AppModel
    let activityFrame: CGRect
    let composerVisible: Bool
    var composerFrame: CGRect? = nil
    let characterPulse: CharacterPulseEvent?
    let characterThrow: CharacterThrowEvent?
    let onCharacterFramesChanged: ([UUID: CGRect]) -> Void
    let onChatBubbleFramesChanged: ([UUID: CGRect]) -> Void

    var body: some View {
        let members = model.pixelWorldMembers
        PixelWorldRepresentable(
            model: model,
            realtimeAvailable: model.activeRoomTransportConnected,
            roomID: model.activeRoom?.id,
            members: members,
            pausedTreeUserIDs: Set(members.filter {
                $0.characterID == PixelCharacterCatalog.pixelTreeID && model.treeMovement.effectivePaused(
                    userID: $0.id, currentUserID: model.currentUserID,
                    legacyPaused: model.preferences.treeMovementPaused)
            }.map(\.id)),
            bubbles: model.activeBubbles,
            edge: model.preferences.overlayRegion.edge,
            activityFrame: activityFrame,
            installationSeed: model.preferences.installationSeed,
            composerVisible: composerVisible,
            composerFrame: composerFrame,
            characterPulse: characterPulse,
            characterThrow: characterThrow,
            onCharacterFramesChanged: onCharacterFramesChanged,
            onChatBubbleFramesChanged: onChatBubbleFramesChanged
        )
    }
}

private struct PixelWorldRepresentable: NSViewRepresentable {
    let model: AppModel
    let realtimeAvailable: Bool
    let roomID: UUID?
    let members: [PixelWorldMember]
    let pausedTreeUserIDs: Set<UUID>
    let bubbles: [ActiveBubble]
    let edge: OverlayEdge
    let activityFrame: CGRect
    let installationSeed: UInt64
    let composerVisible: Bool
    var composerFrame: CGRect? = nil
    let characterPulse: CharacterPulseEvent?
    let characterThrow: CharacterThrowEvent?
    let onCharacterFramesChanged: ([UUID: CGRect]) -> Void
    let onChatBubbleFramesChanged: ([UUID: CGRect]) -> Void

    func makeNSView(context: Context) -> SKView {
        let view = SKView(frame: .zero)
        PixelWorldRendererPolicy.apply(to: view)
        let scene = PixelWorldScene(size: view.bounds.size)
        scene.scaleMode = .resizeFill
        view.presentScene(scene)
        apply(to: scene)
        return view
    }

    func updateNSView(_ view: SKView, context: Context) {
        guard let scene = view.scene as? PixelWorldScene else { return }
        apply(to: scene)
        view.isPaused = false
    }

    static func dismantleNSView(_ view: SKView, coordinator: Void) {
        (view.scene as? PixelWorldScene)?.resetStunState()
        (view.scene as? PixelWorldScene)?.onStopCharacterSounds?()
        view.isPaused = true
        view.presentScene(nil)
    }

    private func apply(to scene: PixelWorldScene) {
        scene.useStunState(model.characterStunState, realtimeAvailable: realtimeAvailable)
        model.characterImpactAudio.isEnabled = model.preferences.characterSoundEffectsEnabled
        scene.onCharacterImpact = { [weak model] id, time in
            model?.characterImpactAudio.play(objectID: id, at: time)
        }
        scene.onStopCharacterSounds = { [weak model] in model?.characterImpactAudio.stopAll() }
        scene.apply(
            roomID: roomID,
            members: members,
            bubbles: bubbles,
            edge: edge,
            activityFrame: activityFrame,
            installationSeed: installationSeed,
            composerVisible: composerVisible,
            composerFrame: composerFrame,
            characterPulse: characterPulse,
            characterThrow: characterThrow,
            onCharacterFramesChanged: onCharacterFramesChanged,
            onChatBubbleFramesChanged: onChatBubbleFramesChanged,
            pausedTreeUserIDs: pausedTreeUserIDs
        )
    }
}
