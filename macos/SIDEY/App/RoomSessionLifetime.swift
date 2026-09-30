import Foundation

@MainActor
final class RoomSessionLifetime {
    var switchPipeline: RoomSwitchPipeline!
    var bootstrapTask: Task<Void, Never>?
    var eventTask: Task<Void, Never>?
    var rolloutTask: Task<Void, Never>?
    var bootstrapRetryCount = 0
    var bootstrapLastSuccessAt: ContinuousClock.Instant?
    var bubbleExpiryTask: Task<Void, Never>?
    var pulseCooldown = CharacterPulseCooldown()
    var throwCooldown = CharacterThrowCooldown()

    func cancel() {
        bootstrapTask?.cancel()
        eventTask?.cancel()
        rolloutTask?.cancel()
        bubbleExpiryTask?.cancel()
        bootstrapTask = nil
        // The stream consumer clears eventTask only after cancellation has drained.
        rolloutTask = nil
        bootstrapRetryCount = 0
        bootstrapLastSuccessAt = nil
        bubbleExpiryTask = nil
        switchPipeline?.cancel()
        pulseCooldown = CharacterPulseCooldown()
        throwCooldown = CharacterThrowCooldown()
    }
}
