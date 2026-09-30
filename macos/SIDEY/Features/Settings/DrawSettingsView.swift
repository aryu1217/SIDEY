import SwiftUI

struct DrawSettingsView: View {
    @Bindable var model: AppModel
    let actions: SettingsActions
    @State private var kind: DrawKind = .lot
    @State private var entryMode: DrawEntryMode = .custom
    @State private var memberIDs: [UUID] = []
    @State private var customEntries: [DrawTextEntry] = [DrawTextEntry(), DrawTextEntry()]
    @State private var destinationLabels: [UUID: String] = [:]
    @State private var pendingDeleteID: UUID?

    private var room: Room? { model.activeRoom }
    private var createTitle: LocalizedStringResource {
        switch kind {
        case .lot: "draw.create.lot"
        case .ladder: "draw.create.ladder"
        case .roulette: "draw.create.roulette"
        }
    }
    private var entryIDs: [UUID] {
        entryMode == .members ? memberIDs : customEntries.map(\.id)
    }
    private var availableMembers: [RoomMember] {
        room?.members.filter { !memberIDs.contains($0.userID) } ?? []
    }
    private func validLabel(_ raw: String) -> Bool {
        let label = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        return (1...80).contains(label.count)
            && !label.contains(where: { "\r\n\t".contains($0) })
    }
    private var canCreate: Bool {
        guard (2...12).contains(entryIDs.count),
              model.connectionState == .online,
              !model.drawOperationInProgress else { return false }
        if entryMode == .members {
            guard Set(memberIDs).count == memberIDs.count,
                  Set(memberIDs).isSubset(of: Set(room?.members.map(\.userID) ?? [])) else { return false }
        } else if !customEntries.allSatisfy({ validLabel($0.text) }) { return false }
        return kind != .ladder || entryIDs.allSatisfy { validLabel(destinationLabels[$0] ?? "") }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 34) {
            SettingsSection(
                title: createTitle,
                subtitle: "draw.create.subtitle",
                systemImage: "dice"
            ) {
                if let room {
                    Picker("draw.kind.label", selection: $kind) {
                        ForEach(DrawKind.allCases, id: \.self) { option in
                            Text(verbatim: option.title).tag(option)
                        }
                    }
                    .pickerStyle(.segmented)

                    Picker("draw.entries.mode", selection: $entryMode) {
                        Text("draw.entries.custom").tag(DrawEntryMode.custom)
                        Text("draw.entries.members").tag(DrawEntryMode.members)
                    }
                    .pickerStyle(.segmented)

                    Text("draw.entries.title").font(.headline)
                    if entryMode == .custom {
                        ForEach($customEntries) { $entry in
                            HStack {
                                TextField("draw.entries.placeholder", text: $entry.text)
                                    .textFieldStyle(.roundedBorder)
                                Button {
                                    customEntries.removeAll { $0.id == entry.id }
                                } label: {
                                    Image(systemName: "minus.circle")
                                }
                                .accessibilityLabel("draw.entries.remove")
                                .disabled(customEntries.count <= 2)
                            }
                        }
                        Button {
                            customEntries.append(DrawTextEntry())
                        } label: {
                            Label("draw.entries.add", systemImage: "plus")
                        }
                        .disabled(customEntries.count >= 12)
                    } else {
                        ForEach(memberIDs, id: \.self) { id in
                            if let member = room.members.first(where: { $0.userID == id }) {
                                HStack {
                                    Text(verbatim: member.nickname)
                                    Spacer()
                                    Button {
                                        memberIDs.removeAll { $0 == id }
                                    } label: {
                                        Image(systemName: "minus.circle")
                                    }
                                    .accessibilityLabel("draw.entries.remove")
                                    .disabled(memberIDs.count <= 2)
                                }
                            }
                        }
                        Menu {
                            ForEach(availableMembers) { member in
                                Button(member.nickname) { memberIDs.append(member.userID) }
                            }
                        } label: {
                            Label("draw.entries.add_member", systemImage: "plus")
                        }
                        .disabled(availableMembers.isEmpty || memberIDs.count >= 12)
                    }
                    if kind == .ladder {
                        Divider()
                        Text("draw.ladder.labels").font(.headline)
                        Text("draw.ladder.hint")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                        ForEach(Array(entryIDs.enumerated()), id: \.element) { entry in
                            HStack {
                                Text(verbatim: "\(entry.offset + 1).")
                                    .frame(width: 35, alignment: .leading)
                                TextField("draw.ladder.placeholder", text: Binding(
                                    get: { destinationLabels[entry.element] ?? "" },
                                    set: { destinationLabels[entry.element] = $0 }
                                ))
                                .textFieldStyle(.roundedBorder)
                            }
                        }
                    }
                    Button {
                        let values = kind == .ladder
                            ? entryIDs.map { (destinationLabels[$0] ?? "").trimmingCharacters(in: .whitespacesAndNewlines) }
                            : nil
                        let entries = entryMode == .custom
                            ? customEntries.map { $0.text.trimmingCharacters(in: .whitespacesAndNewlines) }
                            : nil
                        actions.onCreateDraw(kind, entryMode == .members ? memberIDs : [], entries, values)
                    } label: {
                        if model.drawOperationInProgress { ProgressView() }
                        Text(createTitle)
                    }
                    .buttonStyle(.glassProminent)
                    .disabled(!canCreate)
                } else {
                    ContentUnavailableView("draw.no_room", systemImage: "person.2")
                }
            }

            SettingsSection(
                title: "draw.history.title",
                subtitle: "draw.history.subtitle",
                systemImage: "clock.arrow.circlepath"
            ) {
                HStack {
                    Spacer()
                    Button("draw.refresh", action: actions.onRefreshDraws)
                }
                if model.drawEventsRoomID == room?.id && !model.drawEvents.isEmpty {
                    ForEach(model.drawEvents.sorted(by: { $0.createdAt > $1.createdAt })) { event in
                        HStack(spacing: 12) {
                            Image(systemName: event.kind.symbol)
                                .frame(width: 30)
                            VStack(alignment: .leading, spacing: 4) {
                                Text(verbatim: event.kind.title).font(.headline)
                                Text(verbatim: event.resultLines(in: room).joined(separator: ", "))
                                    .lineLimit(2)
                                if let created = event.createdDate {
                                    Text(created, style: .relative)
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                            }
                            Spacer()
                            Button("draw.replay") { actions.onReplayDraw(event.id) }
                            if event.creatorID == model.currentUserID {
                                Button {
                                    pendingDeleteID = event.id
                                } label: {
                                    Image(systemName: "trash")
                                }
                                .buttonStyle(.borderless)
                                .foregroundStyle(.red)
                                .help("draw.delete")
                                .accessibilityLabel("draw.delete")
                                .disabled(model.drawOperationInProgress)
                            }
                        }
                        .contextMenu {
                            if event.creatorID == model.currentUserID,
                               (event.iconExpiryDate ?? .distantPast) > .now,
                               event.closedAt == nil {
                                Button("draw.end") { actions.onCloseDraw(event.id) }
                            }
                        }
                        if event.id != model.drawEvents.last?.id { Divider() }
                    }
                } else {
                    Text("draw.history.empty").foregroundStyle(.secondary)
                }
            }
        }
        .confirmationDialog("draw.delete.confirm", isPresented: Binding(
            get: { pendingDeleteID != nil },
            set: { if !$0 { pendingDeleteID = nil } }
        )) {
            if let pendingDeleteID {
                Button("draw.delete", role: .destructive) {
                    actions.onDeleteDraw(pendingDeleteID)
                    self.pendingDeleteID = nil
                }
            }
            Button("common.cancel", role: .cancel) { pendingDeleteID = nil }
        } message: {
            Text("draw.delete.detail")
        }
        .task(id: room?.id) {
            // Initialize once per room so later presence updates do not
            // overwrite participant choices the user has made manually.
            let initial = DrawParticipantSelectionPolicy.initialIDs(in: room)
            memberIDs = room?.members.map(\.userID).filter { initial.contains($0) } ?? []
            customEntries = [DrawTextEntry(), DrawTextEntry()]
            destinationLabels = [:]
            actions.onRefreshDraws()
        }
    }
}

private enum DrawEntryMode: Hashable {
    case custom
    case members
}

private struct DrawTextEntry: Identifiable {
    let id = UUID()
    var text = ""
}

enum DrawParticipantSelectionPolicy {
    static func initialIDs(in room: Room?) -> Set<UUID> {
        Set(room?.members.compactMap { member in
            switch member.presence {
            case .online, .typing:
                member.userID
            case .away, .offline, .reconnecting:
                nil
            }
        } ?? [])
    }
}
