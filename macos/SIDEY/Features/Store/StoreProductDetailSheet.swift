import SwiftUI

struct StoreProductDetailSheet: View {
    let productState: CommerceProductState
    var relatedProductState: CommerceProductState? = nil
    var isPurchaseInProgress = false
    let actions: SettingsActions
    var availability: StoreAvailability = .appStore
    var onEquip: (() -> Void)? = nil
    var isEquipDisabled = false
    var isEquipPending = false
    let onClose: () -> Void
    @State private var playsPreviewSound = true
    @State private var contentHeight: CGFloat = 640

    var displaysCommerceAction: Bool { availability.unavailableDetailMessage == nil }

    var body: some View {
        ScrollView {
            VStack(spacing: 16) {
                Text(productState.displayName).font(.title2.bold())
                if productState.product.kind != .character {
                    Text(productState.displayDescription)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
                StorePreviewStage(product: productState.displayProduct,
                    onCharacterImpact: { object, time in
                        if playsPreviewSound { actions.onCharacterImpact(object, time) }
                    }, onStopCharacterSounds: actions.onStopCharacterSounds)
                    .overlay(alignment: .topLeading) {
                        if productState.product.characterID == PixelCharacterCatalog.pixelTreeID {
                            Text(L10n.text("store.preview.tree_toggle_hint"))
                                .font(.caption).foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                                .padding(.trailing, 60).padding(12)
                                .allowsHitTesting(false)
                        }
                    }
                    .overlay(alignment: .topTrailing) {
                        if productState.product.kind != .bubble {
                            Button(playsPreviewSound
                                   ? L10n.text("store.preview.sound.off")
                                   : L10n.text("store.preview.sound.on"),
                                   systemImage: playsPreviewSound ? "speaker.wave.2.fill" : "speaker.slash.fill") {
                                playsPreviewSound.toggle()
                                if !playsPreviewSound { actions.onStopCharacterSounds() }
                            }
                            .labelStyle(.iconOnly)
                            .buttonStyle(.bordered)
                            .help(playsPreviewSound
                                  ? L10n.text("store.preview.sound.off")
                                  : L10n.text("store.preview.sound.on"))
                            .padding(12)
                        }
                    }
                HStack(alignment: .top, spacing: 12) {
                    StoreDetailPurchaseCard(state: productState, actions: actions,
                        availability: availability, purchaseInProgress: isPurchaseInProgress,
                        showsDescription: productState.product.kind == .character,
                        onEquip: onEquip, isEquipDisabled: isEquipDisabled,
                        isEquipPending: isEquipPending)
                    if let relatedProductState {
                        StoreDetailPurchaseCard(state: relatedProductState, actions: actions,
                            availability: availability, purchaseInProgress: isPurchaseInProgress,
                            showsDescription: true)
                    }
                }
                .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.horizontal, 30).padding(.top, 30).padding(.bottom, 24)
            .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { height in
                if height > 0 { contentHeight = height }
            }
        }
        .frame(width: 600, height: min(contentHeight, 720))
        .overlay(alignment: .topTrailing) {
            Button(L10n.text("common.close"), systemImage: "xmark", action: onClose)
                .labelStyle(.iconOnly).buttonStyle(.plain).padding(12)
                .accessibilityLabel(L10n.text("store.product.detail.close"))
        }
        .onDisappear { actions.onStopCharacterSounds() }
    }
}

private struct StoreDetailPurchaseCard: View {
    let state: CommerceProductState
    let actions: SettingsActions
    let availability: StoreAvailability
    let purchaseInProgress: Bool
    var showsDescription = false
    var onEquip: (() -> Void)? = nil
    var isEquipDisabled = false
    var isEquipPending = false

    private var kindLabel: String {
        state.product.isKeepsake ? L10n.text("store.kind.keepsake") : state.product.kind.title
    }
    var body: some View {
        VStack(spacing: 10) {
            HStack {
                Text(kindLabel).font(.caption.weight(.medium))
                Spacer(minLength: 2)
                if state.product.isKeepsake {
                    Text(L10n.text("store.product.sold_separately")).font(.caption2.weight(.semibold))
                        .padding(.horizontal, 8).padding(.vertical, 4)
                        .background(Color.accentColor.opacity(0.14), in: Capsule())
                }
            }
            .frame(height: 22)
            StoreProductPreview(product: state.displayProduct, pointSize: 64).frame(height: 64)
            Text(state.displayName).font(.callout.weight(.semibold))
                .lineLimit(2, reservesSpace: true).multilineTextAlignment(.center)
            if showsDescription {
                Text(state.displayDescription)
                    .font(.caption).foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
            purchaseAction
        }
        .padding(12).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .background(Color.primary.opacity(0.025), in: RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.primary.opacity(0.09)))
    }
    @ViewBuilder private var purchaseAction: some View {
        if let message = availability.unavailableDetailMessage {
            Text(message).font(.caption).foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, minHeight: 30)
        } else if state.isWorking {
            ProgressView().controlSize(.small).frame(maxWidth: .infinity, minHeight: 30)
                .accessibilityLabel(L10n.format("store.product.processing.accessibility", state.displayName))
        } else if state.purchaseState == .owned {
            if isEquipPending {
                ProgressView().controlSize(.small).frame(maxWidth: .infinity, minHeight: 30)
                    .accessibilityLabel(L10n.text("profile.applying"))
            } else if let onEquip, !state.isEquipped {
                Button(L10n.text("profile.equip.action"), action: onEquip)
                    .buttonStyle(.borderedProminent)
                    .disabled(isEquipDisabled)
                    .frame(maxWidth: .infinity, minHeight: 30)
            } else {
                Text(state.isEquipped
                     ? L10n.text("store.product.equipped")
                     : L10n.text("store.purchase.owned"))
                    .font(.callout.weight(.medium)).frame(maxWidth: .infinity, minHeight: 30)
            }
        } else if case .error = state.purchaseState {
            Button(L10n.text("store.status.retry")) { actions.onRefreshCommerceState(state.id) }
                .disabled(purchaseInProgress)
        } else if state.purchaseState == .unavailable {
            VStack(spacing: 6) {
                Text(L10n.text("store.product.unavailable.detail"))
                    .font(.caption).foregroundStyle(.secondary)
                Button(L10n.text("store.status.retry")) { actions.onRefreshCommerceState(state.id) }
                    .disabled(purchaseInProgress)
            }
            .frame(maxWidth: .infinity, minHeight: 30)
        } else if availability.usesAppStore && !state.storefrontProductAvailable {
            VStack(spacing: 6) {
                Text(state.priceLoadState == .loading
                     ? L10n.text("store.price.loading")
                     : L10n.text("store.price.unavailable"))
                    .font(.caption).foregroundStyle(.secondary)
                if state.priceLoadState != .loading {
                    Button(L10n.text("store.price.retry")) { actions.onRefreshCommerceState(state.id) }
                        .disabled(purchaseInProgress)
                }
            }
            .frame(maxWidth: .infinity, minHeight: 30)
        } else {
            Button {
                actions.onPurchase(state.id)
            } label: {
                Text(L10n.format("store.purchase.action", kindLabel, state.formattedPrice))
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .disabled(purchaseInProgress || (availability.usesAppStore && !state.storefrontProductAvailable))
            .accessibilityLabel(L10n.format(
                "store.purchase.accessibility", state.displayName, state.formattedPrice
            ))
        }
    }
}
