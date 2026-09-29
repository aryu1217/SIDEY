import AppKit

enum FirebaseV2CommerceGrantPolicy {
    static func requiresEntitlement(
        kind: CommerceProductKind,
        catalogItemID: String?
    ) -> Bool {
        kind == .throwable && catalogItemID != nil
    }
}

extension AppCoordinator {
    func refreshCommerceState(productID: String? = nil) {
        guard !releaseChannel.isLocalMembershipTest,
              releaseChannel.storeAvailability.allowsCommerceActions,
              let backend
        else { return }
        let requestedIDs = productID.map { [$0] } ?? model.commerceProducts.map(\.id)
        let productIDs = requestedIDs.filter {
            model.commerceProduct(id: $0) != nil && commerceSession.productTasks[$0] == nil
        }
        guard !productIDs.isEmpty else { return }
        productIDs.forEach { model.setCommerceWorking(true, productID: $0) }
        let task = Task { [weak self] in
            guard let self else { return }
            defer {
                for id in productIDs {
                    model.setCommerceWorking(false, productID: id)
                    commerceSession.productTasks[id] = nil
                }
            }
            if releaseChannel.storeAvailability.usesAppStore {
                await refreshAppStorePrices()
            }
            do {
                let states = try await backend.storeState()
                model.applyStoreCatalog(states, usesAppStore: releaseChannel.storeAvailability.usesAppStore)
            } catch is CancellationError {
                return
            } catch {
                model.failStoreCatalogLoading(productIDs: productIDs)
            }
        }
        productIDs.forEach { commerceSession.productTasks[$0] = task }
    }

    func setEquippedCosmetic(kind: CommerceProductKind, catalogItemID: String?) {
        let product = catalogItemID.flatMap { requestedID in
            model.ownedProfileCosmeticProducts(for: kind).first {
                $0.catalogItemID == requestedID
            }
        }
        guard !releaseChannel.isLocalMembershipTest,
              releaseChannel.storeAvailability.allowsCosmeticEquipment,
              kind != .character,
              let backend,
              let messagingTransport,
              catalogItemID == nil || product != nil,
              model.equippedCosmeticID(for: kind) != catalogItemID,
              commerceSession.equipmentTasks[kind] == nil,
              model.beginCosmeticEquipmentRequest(kind: kind, catalogItemID: catalogItemID)
        else { return }

        model.errorMessage = nil
        model.dismissSuccess()
        commerceSession.equipmentTasks[kind] = Task { [weak self] in
            guard let self else { return }
            defer {
                model.endCosmeticEquipmentRequest(kind: kind)
                commerceSession.equipmentTasks[kind] = nil
            }
            do {
                let mutation = try await setEquippedCosmeticForSelectedTransport(
                    backend: backend,
                    messagingTransport: messagingTransport,
                    kind: kind,
                    catalogItemID: catalogItemID
                )
                let profile = mutation.value
                guard profile.id == model.currentUserID else {
                    throw SideyBackendError.malformedResponse
                }
                if let grantError = mutation.grantError {
                    model.errorMessage = L10n.format(
                        "store.error.equipment_realtime_grant_pending",
                        grantError.localizedDescription
                    )
                    return
                }
                model.apply(profile: profile)
                model.presentSuccess(CosmeticEquipmentFeedback.successMessage(
                    kind: kind,
                    product: product
                ))
                model.errorMessage = nil
                persistPreferences()
            } catch is CancellationError {
                return
            } catch {
                model.errorMessage = L10n.text("store.error.equipment_update_failed")
            }
        }
    }


    func purchase(productID: String) {
        guard !releaseChannel.isLocalMembershipTest,
              releaseChannel.storeAvailability.allowsCommerceActions,
              let backend,
              let messagingTransport,
              let productState = model.commerceProduct(id: productID),
              commerceSession.productTasks[productID] == nil,
              productState.purchaseState != .owned,
              !releaseChannel.storeAvailability.usesAppStore || productState.storefrontProductAvailable
        else { return }

        guard productState.purchaseState.canStartPurchase else { return }

        let product = productState.product
        let productDisplayName = productState.displayName
        model.setCommerceWorking(true, productID: productID)
        model.setCommercePurchaseState(.openingCheckout, productID: productID)
        model.errorMessage = nil
        commerceSession.productTasks[productID] = Task { [weak self] in
            guard let self else { return }
            defer {
                model.setCommerceWorking(false, productID: productID)
                commerceSession.productTasks[productID] = nil
            }
            do {
                if releaseChannel.storeAvailability.usesAppStore {
                    guard let userID = model.currentUserID else {
                        throw SideyBackendError.sessionRecoveryFailed
                    }
                    let purchased = try await commerceSession.purchaseController.purchase(
                        productID: productID,
                        userID: userID,
                        accessToken: try await backend.currentAccessToken()
                    )
                    if purchased {
                        let snapshot = try await backend.loadSnapshot()
                        applyBackendSnapshot(snapshot, currentUserID: userID)
                        model.setCommercePurchaseState(.owned, productID: productID)
                        if let equipment = product.automaticEquipmentAfterFreshPurchase {
                            do {
                                let mutation = try await setEquippedCosmeticForSelectedTransport(
                                    backend: backend,
                                    messagingTransport: messagingTransport,
                                    kind: equipment.kind,
                                    catalogItemID: equipment.catalogItemID
                                )
                                let profile = mutation.value
                                guard profile.id == userID else {
                                    throw SideyBackendError.malformedResponse
                                }
                                if let grantError = mutation.grantError {
                                    model.presentSuccess(L10n.format(
                                        "store.purchase.success",
                                        productDisplayName
                                    ))
                                    model.errorMessage = L10n.format(
                                        "store.error.auto_equip_realtime_grant_pending",
                                        grantError.localizedDescription
                                    )
                                    return
                                }
                                model.apply(profile: profile)
                                persistPreferences()
                                model.presentSuccess(L10n.format(
                                    "store.purchase_and_equip.success", productDisplayName
                                ))
                            } catch is CancellationError {
                                return
                            } catch {
                                model.presentSuccess(L10n.format(
                                    "store.purchase.success", productDisplayName
                                ))
                                model.errorMessage = L10n.text("store.error.auto_equip_failed")
                            }
                        } else {
                            model.presentSuccess(L10n.format(
                                "store.purchase.success", productDisplayName
                            ))
                        }
                    } else {
                        model.setCommercePurchaseState(.available, productID: productID)
                    }
                    return
                }
                throw AppStorePurchaseError.verifierNotConfigured
            } catch is CancellationError {
                return
            } catch {
                model.setCommercePurchaseState(
                    .error(L10n.text("store.error.purchase_status_failed")),
                    productID: productID
                )
                model.errorMessage = (error as? AppStorePurchaseError)?.localizedDescription
                    ?? L10n.format("store.error.purchase_failed", productDisplayName)
            }
        }
    }

    func signInWithApple(_ payload: AppleAuthorizationPayload) {
        guard releaseChannel.requiresAppleAuthentication, let backend,
              !model.accountOperationInProgress else { return }
        model.accountOperationInProgress = true
        stopAllTyping()
        let previousTreeTask = cancelTreeMovementRequests()
        model.errorMessage = nil
        Task { [weak self] in
            guard let self else { return }
            defer { model.accountOperationInProgress = false }
            do {
                // Finish cancellation before replacing the session used by the RPC transport.
                await previousTreeTask?.value
                try await backend.signInWithApple(
                    identityToken: payload.identityToken,
                    nonce: payload.nonce
                )
                model.authenticationRequired = false
                startBackend()
            } catch {
                model.authenticationRequired = true
                model.errorMessage = L10n.text("auth.apple_sign_in.failed")
            }
        }
    }

    func restoreAppStorePurchases() {
        guard releaseChannel.requiresAppleAuthentication, let backend, let userID = model.currentUserID,
              !model.accountOperationInProgress else { return }
        model.accountOperationInProgress = true
        model.errorMessage = nil
        Task { [weak self] in
            guard let self else { return }
            defer { model.accountOperationInProgress = false }
            do {
                try await commerceSession.purchaseController.restore(
                    accessToken: try await backend.currentAccessToken()
                )
                let snapshot = try await backend.loadSnapshot()
                applyBackendSnapshot(snapshot, currentUserID: userID)
                refreshCommerceState()
                model.presentSuccess(L10n.text("store.restore.success"))
            } catch {
                model.errorMessage = (error as? AppStorePurchaseError)?.localizedDescription
                    ?? L10n.text("store.restore.failed")
            }
        }
    }

    func deleteAccount(_ payload: AppleAuthorizationPayload) {
        guard releaseChannel.requiresAppleAuthentication, let backend,
              !model.accountOperationInProgress else { return }
        model.accountOperationInProgress = true
        stopAllTyping()
        let previousTreeTask = cancelTreeMovementRequests()
        model.errorMessage = nil
        Task { [weak self] in
            guard let self else { return }
            defer { model.accountOperationInProgress = false }
            do {
                // Finish cancellation before replacing the session used by the RPC transport.
                await previousTreeTask?.value
                try await commerceSession.accountClient.deleteAccount(
                    payload: payload,
                    accessToken: try await backend.currentAccessToken()
                )
                try? await backend.signOut()
                try? KeychainStore(
                    service: releaseChannel.keychainService,
                    session: keychainAccessSession
                ).deleteAll()
                preferencesStore.save(.defaults)
                NSApplication.shared.terminate(nil)
            } catch {
                model.errorMessage = L10n.text("account.delete.failed")
            }
        }
    }

    func refreshAppStorePrices() async {
        model.beginCommercePriceLoading()
        do {
            model.setCommerceStorefrontMetadata(try await commerceSession.purchaseController.loadProducts())
        } catch {
            model.failCommercePriceLoading()
            model.errorMessage = L10n.text("store.error.products_load_failed")
        }
    }

    func configureAppStoreCommerce(backend: SideyBackend) async {
        await refreshAppStorePrices()
        do {
            try await commerceSession.purchaseController.reconcileCurrentEntitlements(
                accessToken: try await backend.currentAccessToken()
            )
        } catch {
            model.errorMessage = L10n.text("store.error.entitlements_reconcile_failed")
        }
        commerceSession.purchaseController.startObserving(
            accessToken: { try await backend.currentAccessToken() },
            didChange: { [weak self] in self?.refreshCommerceState() },
            didFail: { [weak self] in
                self?.model.errorMessage = L10n.text("store.error.transaction_update_failed")
            }
        )
    }

    private func setEquippedCosmeticForSelectedTransport(
        backend: SideyBackend,
        messagingTransport: RoomMessagingTransportRouter,
        kind: CommerceProductKind,
        catalogItemID: String?
    ) async throws -> RealtimeGrantAwareMutationResult<Profile> {
        try await RealtimeGrantAwareMutation.perform(
            selection: try await messagingTransport.currentSelection(),
            // Only throwable ownership is embedded in the Firebase v2 wire
            // authorization grant. Bubble styles remain a server-authorized
            // chat attribute and must not close the complete realtime plane.
            requiresEntitlement: FirebaseV2CommerceGrantPolicy.requiresEntitlement(
                kind: kind,
                catalogItemID: catalogItemID
            ),
            legacy: {
                try await backend.setEquippedCosmetic(
                    kind: kind,
                    catalogItemID: catalogItemID
                )
            },
            firebaseV2: {
                let grant = try await backend.setEquippedCosmeticV2(
                    kind: kind,
                    catalogItemID: catalogItemID
                )
                return (grant.profile, grant.accessRevision)
            },
            converge: { revision, requiresEntitlement in
                try await messagingTransport.convergeAccessGrant(
                    revision: revision,
                    requiresEntitlement: requiresEntitlement
                )
            }
        )
    }
}
