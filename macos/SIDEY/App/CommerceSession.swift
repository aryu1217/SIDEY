import Foundation

@MainActor
final class CommerceSession {
    let purchaseController = AppStorePurchaseController()
    let accountClient = AppStoreAccountClient()
    var productTasks: [String: Task<Void, Never>] = [:]
    var equipmentTasks: [CommerceProductKind: Task<Void, Never>] = [:]
    var characterTask: Task<Void, Never>?
    var configurationTask: Task<Void, Never>?
    var configurationID: UUID?

    func cancelConfiguration() {
        configurationID = nil
        configurationTask?.cancel()
        configurationTask = nil
        purchaseController.stopObserving()
    }

    func cancel(model: AppModel) {
        cancelConfiguration()
        productTasks.values.forEach { $0.cancel() }
        productTasks.removeAll()
        equipmentTasks.values.forEach { $0.cancel() }
        for kind in equipmentTasks.keys { model.endCosmeticEquipmentRequest(kind: kind) }
        equipmentTasks.removeAll()
        characterTask?.cancel()
        characterTask = nil
        model.endCharacterEquipmentRequest()
    }
}
