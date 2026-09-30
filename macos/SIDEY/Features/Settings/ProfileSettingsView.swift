import SwiftUI

struct ProfileSettingsView: View {
    @Bindable var model: AppModel
    let actions: SettingsActions
    let storeAvailability: StoreAvailability

    private var bubbleProducts: [CommerceProduct] {
        model.ownedProfileCosmeticProducts(for: .bubble)
    }

    private var throwableProducts: [CommerceProduct] {
        model.ownedProfileCosmeticProducts(for: .throwable)
    }

    private var showsCosmeticEquipment: Bool {
        ProfileCosmeticEquipmentPolicy.shouldShow(
            availability: storeAvailability
        )
    }

    var body: some View {
        SettingsSection(
            title: "profile.title",
            subtitle: "profile.subtitle",
            systemImage: "person.crop.circle"
        ) {
            SettingsControlRow(
                title: "profile.nickname.label",
                description: "profile.nickname.description"
            ) {
                VStack(alignment: .trailing, spacing: 10) {
                    TextField("profile.nickname.placeholder", text: $model.nickname)
                        .textFieldStyle(.roundedBorder)
                        .frame(maxWidth: .infinity)
                        .onChange(of: model.nickname) { _, value in
                            let limited = ProfileValidator.limitedNicknameDraft(value)
                            if limited != value { model.nickname = limited }
                        }
                    if model.hasNicknameChanges {
                        Button("profile.nickname.save") {
                            PendingTextInputCommitter.commitThen(actions.onSaveProfile)
                        }
                        .buttonStyle(.glassProminent)
                        .disabled(model.groupMutationsDisabled || !validNickname)
                    }
                }
            }
            Divider()
            VStack(alignment: .leading, spacing: 6) {
                Text("profile.character.label")
                    .font(.headline)
                Text("profile.character.description")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            CharacterSelectionGrid(
                maximumColumns: 5,
                characters: model.selectableCharacters,
                confirmedSelection: model.selectedCharacterID,
                pendingSelection: model.pendingCharacterID,
                isDisabled: model.pendingCharacterID != nil || model.groupMutationsDisabled,
                onSelect: actions.onSetCharacter
            )
            Text("profile.duplicates_allowed")
                .font(.caption)
                .foregroundStyle(.secondary)
            if showsCosmeticEquipment {
                Divider()
                ProfileCosmeticEquipmentSection(
                    kind: .bubble,
                    products: bubbleProducts,
                    selectedCatalogItemID: model.equippedBubbleStyleID,
                    pendingRequest: model.cosmeticEquipmentRequest(for: .bubble),
                    selectedCharacterID: model.selectedCharacterID,
                    onSelect: actions.onSetEquippedCosmetic
                )
                Divider()
                ProfileCosmeticEquipmentSection(
                    kind: .throwable,
                    products: throwableProducts,
                    selectedCatalogItemID: model.equippedThrowableID,
                    pendingRequest: model.cosmeticEquipmentRequest(for: .throwable),
                    selectedCharacterID: model.selectedCharacterID,
                    onSelect: actions.onSetEquippedCosmetic
                )
            }
        }

        if !model.preferences.onboardingComplete {
            Label("profile.onboarding.next_hint", systemImage: "sparkles")
                .foregroundStyle(.secondary)
                .padding(.top, 18)
        }
    }

    private var validNickname: Bool {
        ProfileValidator.isValidNickname(model.nickname)
    }
}

struct ProfileCosmeticEquipmentSection: View {
    let kind: CommerceProductKind
    let products: [CommerceProduct]
    let selectedCatalogItemID: String?
    let pendingRequest: CosmeticEquipmentRequest?
    let selectedCharacterID: String
    let onSelect: (CommerceProductKind, String?) -> Void

    private let columns = Array(
        repeating: GridItem(.flexible(minimum: 76), spacing: 10, alignment: .top),
        count: 4
    )

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 5) {
                Text(kind.localizedTitle)
                    .font(.headline)
                Text(description)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }

            LazyVGrid(columns: columns, alignment: .leading, spacing: 10) {
                ProfileCosmeticTile(
                    kind: kind,
                    product: nil,
                    selectedCharacterID: selectedCharacterID,
                    isSelected: selectedCatalogItemID == nil,
                    isPending: pendingRequest?.catalogItemID == nil && pendingRequest != nil,
                    isDisabled: pendingRequest != nil,
                    onSelect: onSelect
                )
                ForEach(products, id: \.id) { product in
                    ProfileCosmeticTile(
                        kind: kind,
                        product: product,
                        selectedCharacterID: selectedCharacterID,
                        isSelected: selectedCatalogItemID == product.catalogItemID,
                        isPending: pendingRequest?.catalogItemID == product.catalogItemID,
                        isDisabled: pendingRequest != nil,
                        onSelect: onSelect
                    )
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel(Text(kind.selectionAccessibilityLabel))
        }
    }

    private var description: LocalizedStringResource {
        switch kind {
        case .bubble: "profile.cosmetics.bubble.description"
        case .throwable: "profile.cosmetics.throwable.description"
        case .character: "profile.cosmetics.character.description"
        }
    }
}

struct ProfileCosmeticTile: View {
    let kind: CommerceProductKind
    let product: CommerceProduct?
    let selectedCharacterID: String
    let isSelected: Bool
    let isPending: Bool
    let isDisabled: Bool
    let onSelect: (CommerceProductKind, String?) -> Void
    @FocusState private var isFocused: Bool

    var body: some View {
        Button(action: requestSelection) {
            VStack(spacing: 7) {
                preview
                    .frame(height: 58)
                label
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.primary)
                    .lineLimit(2)
                    .multilineTextAlignment(.center)
                    .minimumScaleFactor(0.8)
                    .frame(maxWidth: .infinity, minHeight: 30)
            }
            .padding(9)
            .frame(maxWidth: .infinity, minHeight: 112)
            .modifier(ProfileSelectionAppearance(
                isSelected: isSelected,
                isPending: isPending,
                isFocused: isFocused
            ))
            .contentShape(RoundedRectangle(cornerRadius: 13, style: .continuous))
        }
        .buttonStyle(.plain)
        .focused($isFocused)
        .disabled(isDisabled)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibilityLabel)
        .accessibilityValue(Text(accessibilityValue))
        .accessibilityHint(Text(accessibilityHint))
    }

    @ViewBuilder private var preview: some View {
        if let product {
            StoreProductPreview(product: product, pointSize: 56)
        } else {
            switch kind {
            case .bubble:
                StoreBubblePreview(styleID: nil)
                    .frame(width: 56, height: 28)
            case .throwable:
                StoreThrowablePreview(
                    objectID: PixelCharacterThrowCatalog.objectID(for: selectedCharacterID),
                    pointSize: 56
                )
            case .character:
                EmptyView()
            }
        }
    }

    @ViewBuilder private var label: some View {
        if let product {
            Text(verbatim: product.displayName)
        } else {
            Text(defaultLabel)
        }
    }

    private var accessibilityLabel: Text {
        if let product {
            return Text(verbatim: product.displayName)
        }
        return Text(defaultLabel)
    }

    private var defaultLabel: LocalizedStringResource {
        kind == .bubble
            ? "profile.cosmetics.default_bubble"
            : "profile.cosmetics.default_throwable"
    }

    private var accessibilityValue: LocalizedStringResource {
        if isPending { return "profile.applying" }
        return isSelected
            ? "profile.selection.state.selected"
            : "profile.selection.state.not_selected"
    }

    private var accessibilityHint: LocalizedStringResource {
        isSelected
            ? "profile.cosmetics.hint.current"
            : "profile.cosmetics.hint.select"
    }

    func requestSelection() {
        guard !isDisabled, !isSelected else { return }
        onSelect(kind, product?.catalogItemID)
    }
}

private extension CommerceProductKind {
    var localizedTitle: LocalizedStringResource {
        switch self {
        case .character: "commerce.kind.character"
        case .bubble: "commerce.kind.bubble"
        case .throwable: "commerce.kind.throwable"
        }
    }

    var selectionAccessibilityLabel: LocalizedStringResource {
        switch self {
        case .character: "profile.cosmetics.character.selection.accessibility"
        case .bubble: "profile.cosmetics.bubble.selection.accessibility"
        case .throwable: "profile.cosmetics.throwable.selection.accessibility"
        }
    }
}

enum ProfileCosmeticEquipmentPolicy {
    static func shouldShow(availability: StoreAvailability) -> Bool {
        availability.allowsCosmeticEquipment
    }
}
