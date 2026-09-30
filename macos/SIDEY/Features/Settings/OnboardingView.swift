import SwiftUI

struct OnboardingView: View {
    @Bindable var model: AppModel
    let actions: SettingsActions
    @State private var groupPath: GroupPath = .create

    var body: some View {
        ZStack {
            LinearGradient(
                colors: [Color.mint.opacity(0.16), Color.cyan.opacity(0.08), Color.clear],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            )
            .ignoresSafeArea()

            ScrollView {
                VStack(spacing: 28) {
                    HStack(spacing: 8) {
                        stepBadge(number: 1, title: "onboarding.step.profile", complete: model.hasProfile)
                        Image(systemName: "chevron.right").foregroundStyle(.tertiary)
                        stepBadge(number: 2, title: "onboarding.step.group", complete: model.preferences.onboardingComplete)
                    }

                    VStack(spacing: 8) {
                        Text(headerTitle)
                            .font(.system(size: 34, weight: .bold, design: .rounded))
                        Text(headerDescription)
                            .font(.title3)
                            .foregroundStyle(.secondary)
                    }

                    Group {
                        if model.hasProfile {
                            groupStep
                        } else {
                            profileStep
                        }
                    }
                    .padding(26)
                    .frame(width: 620, alignment: .leading)
                    .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
                    .glassEffect(in: RoundedRectangle(cornerRadius: 14, style: .continuous))

                    ConnectionControls(
                        state: model.connectionState,
                        onReconnect: actions.onReconnect
                    )
                        .frame(width: 260)
                }
                .frame(maxWidth: .infinity)
                .padding(48)
            }

            if let error = model.errorMessage {
                ErrorBanner(message: error) { model.errorMessage = nil }
                    .padding(20)
                    .frame(maxHeight: .infinity, alignment: .bottom)
            } else if let success = model.successMessage {
                SuccessBanner(message: success) { model.dismissSuccess() }
                    .padding(20)
                    .frame(maxHeight: .infinity, alignment: .bottom)
            }
        }
        .task(id: model.successMessageGeneration) {
            let generation = model.successMessageGeneration
            guard model.successMessage != nil else { return }
            do {
                try await Task.sleep(for: SuccessFeedbackState.displayDuration)
            } catch {
                return
            }
            model.dismissSuccess(generation: generation)
        }
    }

    private var profileStep: some View {
        VStack(alignment: .leading, spacing: 22) {
            Text("onboarding.character.label")
                .font(.title2.bold())
            CharacterSelectionGrid(
                maximumColumns: 4,
                characters: model.selectableCharacters,
                confirmedSelection: model.selectedCharacterID,
                onSelect: { model.selectedCharacterID = $0 }
            )
            TextField("onboarding.profile.nickname.placeholder", text: $model.nickname)
                .textFieldStyle(.roundedBorder)
                .font(.title3)
                .onChange(of: model.nickname) { _, value in
                    let limited = ProfileValidator.limitedNicknameDraft(value)
                    if limited != value { model.nickname = limited }
                }
            HStack {
                Spacer()
                Button("common.next") {
                    PendingTextInputCommitter.commitThen(actions.onSaveProfile)
                }
                    .buttonStyle(.glassProminent)
                    .disabled(model.isWorking || !validNickname)
            }
        }
    }

    private var groupStep: some View {
        VStack(alignment: .leading, spacing: 20) {
            Picker("onboarding.group.path.accessibility", selection: $groupPath) {
                ForEach(GroupPath.allCases) { path in
                    Text(path.title).tag(path)
                }
            }
            .pickerStyle(.segmented)

            if groupPath == .create {
                TextField("groups.create.name.placeholder", text: $model.newRoomName)
                    .textFieldStyle(.roundedBorder)
                HStack {
                    Text("onboarding.group.create.description").foregroundStyle(.secondary)
                    Spacer()
                    Button(action: actions.onCreateRoom) {
                        OperationButtonLabel(
                            title: model.groupOperation.localizedCreateButtonTitle,
                            showsProgress: model.groupOperation == .creating
                        )
                    }
                        .buttonStyle(.glassProminent)
                        .disabled(model.groupMutationsDisabled || !validRoomName)
                }
            } else {
                TextField("onboarding.group.invite_code.placeholder", text: $model.inviteCode)
                    .textFieldStyle(.roundedBorder)
                    .onChange(of: model.inviteCode) { _, value in
                        let uppercased = value.uppercased()
                        if value != uppercased { model.inviteCode = uppercased }
                    }
                HStack {
                    Text(verbatim: L10n.format(
                        "onboarding.group.member_limit",
                        ProductLimits.maximumRoomMembers
                    ))
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button(action: actions.onJoinRoom) {
                        OperationButtonLabel(
                            title: model.groupOperation.localizedJoinButtonTitle,
                            showsProgress: model.groupOperation == .joining
                        )
                    }
                        .buttonStyle(.glassProminent)
                        .disabled(model.groupMutationsDisabled || model.inviteCode.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
    }

    private func stepBadge(
        number: Int,
        title: LocalizedStringResource,
        complete: Bool
    ) -> some View {
        HStack(spacing: 7) {
            Image(systemName: complete ? "checkmark.circle.fill" : "\(number).circle.fill")
            Text(title)
        }
        .font(.headline)
        .foregroundStyle(complete ? .mint : .primary)
    }

    private var validNickname: Bool {
        ProfileValidator.isValidNickname(model.nickname)
    }

    private var validRoomName: Bool {
        RoomNameValidator.isValid(model.newRoomName)
    }

    private var headerTitle: LocalizedStringResource {
        model.hasProfile
            ? "onboarding.group.title"
            : "onboarding.profile.title"
    }

    private var headerDescription: LocalizedStringResource {
        model.hasProfile
            ? "onboarding.group.description"
            : "onboarding.profile.description"
    }

    private enum GroupPath: String, CaseIterable, Identifiable {
        case create
        case join

        var id: String { rawValue }
        var title: LocalizedStringResource {
            self == .create
                ? "groups.create.action"
                : "groups.action.join_by_code"
        }
    }
}

extension GroupOperation {
    var localizedCreateButtonTitle: LocalizedStringResource {
        self == .creating
            ? "groups.operation.creating"
            : "groups.create.action"
    }

    var localizedJoinButtonTitle: LocalizedStringResource {
        self == .joining
            ? "groups.operation.joining"
            : "groups.action.join_by_code"
    }
}
