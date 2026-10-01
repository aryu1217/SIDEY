import SpriteKit
import SwiftUI

struct PixelWorldView: View {
    @Bindable var model: AppModel
    let renderView: PixelWorldRenderView
    var dragBridge: CharacterDragBridge? = nil
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
            renderView: renderView,
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
            dragBridge: dragBridge,
            activityFrame: activityFrame,
            installationSeed: model.preferences.installationSeed,
            composerVisible: composerVisible,
            composerFrame: dragBridge?.composerFrame ?? composerFrame,
            characterPulse: characterPulse,
            characterThrow: characterThrow,
            onCharacterFramesChanged: onCharacterFramesChanged,
            onChatBubbleFramesChanged: onChatBubbleFramesChanged
        )
    }
}

/// SwiftUI observes model inputs only. AppKit owns the sibling rendering view's
/// geometry, so a SwiftUI layout pass cannot resize a live Metal drawable.
private struct PixelWorldRepresentable: NSViewRepresentable {
    let model: AppModel
    let renderView: PixelWorldRenderView
    let realtimeAvailable: Bool
    let roomID: UUID?
    let members: [PixelWorldMember]
    let pausedTreeUserIDs: Set<UUID>
    let bubbles: [ActiveBubble]
    let edge: OverlayEdge
    var dragBridge: CharacterDragBridge? = nil
    let activityFrame: CGRect
    let installationSeed: UInt64
    let composerVisible: Bool
    var composerFrame: CGRect? = nil
    let characterPulse: CharacterPulseEvent?
    let characterThrow: CharacterThrowEvent?
    let onCharacterFramesChanged: ([UUID: CGRect]) -> Void
    let onChatBubbleFramesChanged: ([UUID: CGRect]) -> Void

    func makeNSView(context: Context) -> NSView {
        apply()
        return NSView(frame: .zero)
    }

    func updateNSView(_ view: NSView, context: Context) {
        apply()
    }

    private func apply() {
        let view = renderView
        let scene = view.scene
        scene.dragBridge = dragBridge
        dragBridge?.scene = scene
        view.dragBridge = dragBridge
        dragBridge?.renderView = view
        view.synchronizeViewport()
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
            activityFrame: view.appliedActivityFrame ?? activityFrame,
            installationSeed: installationSeed,
            composerVisible: composerVisible,
            composerFrame: dragBridge?.composerFrame ?? composerFrame,
            characterPulse: characterPulse,
            characterThrow: characterThrow,
            onCharacterFramesChanged: onCharacterFramesChanged,
            onChatBubbleFramesChanged: onChatBubbleFramesChanged,
            pausedTreeUserIDs: pausedTreeUserIDs
        )
    }
}
