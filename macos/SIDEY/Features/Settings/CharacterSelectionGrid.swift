import SwiftUI

struct CharacterSelectionGrid: View {
    let maximumColumns: Int
    let characters: [PixelCharacterDefinition]
    let confirmedSelection: String
    let pendingSelection: String?
    let isDisabled: Bool
    let onSelect: (String) -> Void
    let onShowDetail: ((String) -> Void)?

    init(
        maximumColumns: Int,
        characters: [PixelCharacterDefinition] = PixelCharacterCatalog.free,
        confirmedSelection: String,
        pendingSelection: String? = nil,
        isDisabled: Bool = false,
        onSelect: @escaping (String) -> Void,
        onShowDetail: ((String) -> Void)? = nil
    ) {
        self.maximumColumns = maximumColumns
        self.characters = characters
        self.confirmedSelection = confirmedSelection
        self.pendingSelection = pendingSelection
        self.isDisabled = isDisabled
        self.onSelect = onSelect
        self.onShowDetail = onShowDetail
    }

    private var columns: [GridItem] {
        Array(
            repeating: GridItem(.flexible(minimum: 92, maximum: 132), spacing: 12),
            count: maximumColumns
        )
    }

    var body: some View {
        LazyVGrid(columns: columns, alignment: .leading, spacing: 12) {
            ForEach(characters) { character in
                CharacterSelectionCard(
                    character: character,
                    isSelected: PixelCharacterCatalog.canonicalID(for: confirmedSelection) == character.id,
                    isPending: pendingSelection.map {
                        PixelCharacterCatalog.canonicalID(for: $0) == character.id
                    } ?? false,
                    isDisabled: isDisabled,
                    onSelect: { onSelect(character.id) },
                    onShowDetail: onShowDetail.map { showDetail in
                        { showDetail(character.id) }
                    }
                )
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("profile.character.selection.accessibility")
    }
}

private struct CharacterSelectionCard: View {
    let character: PixelCharacterDefinition
    let isSelected: Bool
    let isPending: Bool
    let isDisabled: Bool
    let onSelect: () -> Void
    let onShowDetail: (() -> Void)?

    var body: some View {
        Button(action: {
            if character.entitlementKey != nil, let onShowDetail {
                onShowDetail()
            } else {
                onSelect()
            }
        }) {
            VStack(spacing: 8) {
                Image(nsImage: PixelCharacterPreviewImage.image(for: character))
                    .interpolation(.none)
                    .resizable()
                    .frame(width: 72, height: 72)
                    .accessibilityHidden(true)
                Text(localizedName)
                    .font(.system(size: 13, weight: .semibold, design: .rounded))
                    .foregroundStyle(.primary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 12)
            .modifier(ProfileSelectionAppearance(
                isSelected: isSelected,
                isPending: isPending
            ))
            .contentShape(RoundedRectangle(cornerRadius: 13, style: .continuous))
        }
        .buttonStyle(.plain)
        .disabled(isDisabled)
        .accessibilityLabel(Text(localizedName))
        .accessibilityValue(Text(accessibilityValue))
        .accessibilityHint(character.entitlementKey != nil && onShowDetail != nil
            ? L10n.text("store.product.detail.hint") : "")
    }

    private var localizedName: LocalizedStringResource {
        switch character.id {
        case "pixel_hamster": "character.pixel_hamster.display_name"
        case "pixel_cat": "character.pixel_cat.display_name"
        case "pixel_puppy": "character.pixel_puppy.display_name"
        case "pixel_rabbit": "character.pixel_rabbit.display_name"
        case "pixel_penguin": "character.pixel_penguin.display_name"
        case "pixel_guinea_pig": "character.pixel_guinea_pig.display_name"
        case "pixel_monkey": "character.pixel_monkey.display_name"
        case "pixel_chinchilla": "character.pixel_chinchilla.display_name"
        case "pixel_starlight_upalupa": "character.pixel_starlight_upalupa.display_name"
        case "pixel_otter": "character.pixel_otter.display_name"
        case "pixel_pig": "character.pixel_pig.display_name"
        case "pixel_tree": "character.pixel_tree.display_name"
        case "pixel_shiba": "character.pixel_shiba.display_name"
        case "pixel_duck": "character.pixel_duck.display_name"
        case "pixel_poop": "character.pixel_poop.display_name"
        case "pixel_tteokbokki": "character.pixel_tteokbokki.display_name"
        case "pixel_quokka": "character.pixel_quokka.display_name"
        default: "character.unknown.display_name"
        }
    }

    private var accessibilityValue: LocalizedStringResource {
        if isPending { return "profile.character.state.equipping" }
        return isSelected
            ? "profile.selection.state.selected"
            : "profile.selection.state.not_selected"
    }
}
