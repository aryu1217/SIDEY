import AppKit
import Metal
import QuartzCore
import SpriteKit

/// Presents the replacement drawable in the same transaction as a window resize.
/// SKView owns that presentation internally, so synchronizing only its scene size
/// cannot prevent an old drawable from being stretched during the transition.
@MainActor
final class PixelWorldRenderView: NSView {
    let scene: PixelWorldScene
    weak var dragBridge: CharacterDragBridge?
    private(set) var appliedActivityFrame: CGRect?

    private let metalLayer = CAMetalLayer()
    private let commandQueue: (any MTLCommandQueue)?
    private let renderer: SKRenderer?
    private var depthStencilTexture: (any MTLTexture)?
    private var renderingRequested = false
    private var isDrawingFrame = false
    private var presentedViewport: Viewport?

    private struct Viewport: Equatable {
        let size: CGSize
        let activityFrame: CGRect
        let scale: CGFloat
    }

    private final class Resources {
        var timer: Timer?
    }
    private let resourceLifetime = MainActorResourceLifetime(Resources()) { $0.timer?.invalidate() }

    override init(frame frameRect: NSRect) {
        let device = MTLCreateSystemDefaultDevice()
        commandQueue = device?.makeCommandQueue()
        renderer = device.map { SKRenderer(device: $0) }
        scene = PixelWorldScene(size: frameRect.size)
        super.init(frame: frameRect)
        scene.scaleMode = .fill
        renderer?.scene = scene
        renderer?.ignoresSiblingOrder = true
        renderer?.shouldCullNonVisibleNodes = true
        metalLayer.device = device
        metalLayer.pixelFormat = .bgra8Unorm
        metalLayer.isOpaque = false
        metalLayer.backgroundColor = NSColor.clear.cgColor
        metalLayer.framebufferOnly = true
        metalLayer.presentsWithTransaction = true
        metalLayer.allowsNextDrawableTimeout = true
        metalLayer.displaySyncEnabled = true
        metalLayer.contentsGravity = .bottomLeft
        wantsLayer = true
        if device == nil || commandQueue == nil {
            NSLog("SIDEY overlay: Metal renderer is unavailable")
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { nil }

    override var isOpaque: Bool { false }
    override var wantsUpdateLayer: Bool { true }
    override func makeBackingLayer() -> CALayer { metalLayer }

    // AppKit layout may request several layer updates in one transaction. The
    // window controller submits exactly one resize frame after layout completes;
    // ordinary animation frames are driven by the bounded timer below.
    override func updateLayer() {}

    var isViewportReady: Bool {
        guard let viewport = requestedViewport else { return false }
        return bounds.size == viewport.size && scene.size == viewport.size
            && appliedActivityFrame == viewport.activityFrame
    }

    private var requestedViewport: Viewport? {
        guard let viewport = dragBridge?.viewport,
              viewport.size.width.isFinite, viewport.size.height.isFinite,
              viewport.size.width > 0, viewport.size.height > 0 else { return nil }
        return Viewport(size: viewport.size, activityFrame: viewport.activityFrame,
                        scale: window?.backingScaleFactor ?? 1)
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        synchronizeViewport()
    }

    override func setBoundsSize(_ newSize: NSSize) {
        super.setBoundsSize(newSize)
        synchronizeViewport()
    }

    override func layout() {
        super.layout()
        synchronizeViewport()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        synchronizeViewport()
        updateTimer()
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        synchronizeViewport()
        drawFrame()
    }

    func synchronizeViewport() {
        guard !isDrawingFrame, let viewport = requestedViewport,
              bounds.size == viewport.size else { return }
        appliedActivityFrame = viewport.activityFrame
        scene.setViewport(size: viewport.size, activityFrame: viewport.activityFrame)
        metalLayer.contentsScale = viewport.scale
        metalLayer.drawableSize = CGSize(width: (viewport.size.width * viewport.scale).rounded(),
                                         height: (viewport.size.height * viewport.scale).rounded())
    }

    func startRendering() {
        renderingRequested = true
        scene.isPaused = false
        renderer?.scene = scene
        updateTimer()
    }

    func stopRendering() {
        renderingRequested = false
        updateTimer()
        scene.cancelCharacterDrags()
        scene.resetStunState()
        scene.onStopCharacterSounds?()
        renderer?.scene = nil
        depthStencilTexture = nil
    }

    private func depthStencilAttachment(for colorTexture: any MTLTexture) -> (any MTLTexture)? {
        if let texture = depthStencilTexture,
           texture.width == colorTexture.width,
           texture.height == colorTexture.height,
           texture.sampleCount == colorTexture.sampleCount {
            return texture
        }
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(
            pixelFormat: .depth32Float_stencil8,
            width: colorTexture.width,
            height: colorTexture.height,
            mipmapped: false
        )
        descriptor.sampleCount = colorTexture.sampleCount
        descriptor.storageMode = .private
        descriptor.usage = .renderTarget
        depthStencilTexture = colorTexture.device.makeTexture(descriptor: descriptor)
        depthStencilTexture?.label = "SIDEY SpriteKit depth/stencil"
        return depthStencilTexture
    }

    private func updateTimer() {
        let resources = resourceLifetime.resource
        guard renderingRequested, window != nil, renderer != nil, commandQueue != nil else {
            resources.timer?.invalidate()
            resources.timer = nil
            return
        }
        guard resources.timer == nil else { return }
        let timer = Timer(timeInterval: 1 / Double(PixelWorldRendererPolicy.preferredFramesPerSecond),
                          repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.window?.isVisible == true else { return }
                self.drawFrame(advanceSimulation: true)
            }
        }
        resources.timer = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    /// Resize redraws do not advance physics or actions. In particular, a landing
    /// callback must never recursively enter SKRenderer.update(atTime:).
    @discardableResult
    func drawFrame(advanceSimulation: Bool = false) -> Bool {
        guard renderingRequested, !isDrawingFrame, window != nil else { return false }
        synchronizeViewport()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        defer { CATransaction.commit() }
        guard isViewportReady, let viewport = requestedViewport,
              let renderer, let commandQueue else {
            metalLayer.isHidden = true
            return false
        }
        guard !scene.isRenderingSuspended else {
            metalLayer.isHidden = presentedViewport != viewport
            return false
        }
        isDrawingFrame = true
        defer { isDrawingFrame = false }
        return autoreleasepool {
            guard let drawable = metalLayer.nextDrawable(),
                  let depthStencil = depthStencilAttachment(for: drawable.texture),
                  let buffer = commandQueue.makeCommandBuffer() else {
                // Keep the last frame only if its geometry is still valid. Never
                // display an old-sized buffer stretched over the resized window.
                metalLayer.isHidden = presentedViewport != viewport
                return false
            }
            if advanceSimulation {
                renderer.update(atTime: ProcessInfo.processInfo.systemUptime)
            }
            let pass = MTLRenderPassDescriptor()
            pass.colorAttachments[0].texture = drawable.texture
            pass.colorAttachments[0].loadAction = .clear
            pass.colorAttachments[0].storeAction = .store
            pass.colorAttachments[0].clearColor = MTLClearColorMake(0, 0, 0, 0)
            // SpriteKit's filled shapes (including nameplates) require stencil
            // storage. Keep both attachments matched to the acquired drawable,
            // including the first frame after a resize or backing-scale change.
            pass.depthAttachment.texture = depthStencil
            pass.depthAttachment.loadAction = .clear
            pass.depthAttachment.storeAction = .dontCare
            pass.depthAttachment.clearDepth = 1
            pass.stencilAttachment.texture = depthStencil
            pass.stencilAttachment.loadAction = .clear
            pass.stencilAttachment.storeAction = .dontCare
            pass.stencilAttachment.clearStencil = 0
            let drawableSize = CGSize(width: drawable.texture.width, height: drawable.texture.height)
            renderer.render(withViewport: CGRect(origin: .zero, size: drawableSize),
                            commandBuffer: buffer, renderPassDescriptor: pass)
            // With presentsWithTransaction, present the drawable directly after
            // scheduling GPU work; commandBuffer.present would bypass this CA
            // transaction and reintroduce a frame of mismatched window geometry.
            buffer.commit()
            buffer.waitUntilScheduled()
            guard buffer.status != .error else {
                metalLayer.isHidden = presentedViewport != viewport
                return false
            }
            drawable.present()
            presentedViewport = viewport
            metalLayer.isHidden = false
            return true
        }
    }
}
