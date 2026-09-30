import SwiftUI

struct DrawSettingsView: View {
    @Bindable var model: AppModel
    let actions: SettingsActions
    @State private var kind: DrawKind = .lot
    @State private var selectedIDs: Set<UUID> = []
    @State private var labels: [UUID: String] = [:]

    private var room: Room? { model.activeRoom }
    private var createTitle: LocalizedStringResource {
        switch kind {
        case .lot: "draw.create.lot"
        case .ladder: "draw.create.ladder"
        case .roulette: "draw.create.roulette"
        }
    }
    private var participants: [RoomMember] {
        room?.members.filter { selectedIDs.contains($0.userID) } ?? []
    }
    private var canCreate: Bool {
        guard (2...12).contains(participants.count),
              model.connectionState == .online,
              !model.drawOperationInProgress else { return false }
        return kind != .ladder || participants.allSatisfy {
            let label = (labels[$0.userID] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            return (1...80).contains(label.count)
                && !label.contains(where: { "\r\n\t".contains($0) })
        }
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

                    Text("draw.participants.title").font(.headline)
                    ForEach(room.members) { member in
                        Toggle(isOn: Binding(
                            get: { selectedIDs.contains(member.userID) },
                            set: { enabled in
                                if enabled { selectedIDs.insert(member.userID) }
                                else { selectedIDs.remove(member.userID) }
                            }
                        )) {
                            Text(verbatim: member.nickname)
                        }
                    }
                    if kind == .ladder {
                        Divider()
                        Text("draw.ladder.labels").font(.headline)
                        Text("draw.ladder.hint")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                        ForEach(participants.indices, id: \.self) { index in
                            let member = participants[index]
                            HStack {
                                Text(verbatim: "\(index + 1).")
                                    .frame(width: 35, alignment: .leading)
                                TextField("draw.ladder.placeholder", text: Binding(
                                    get: { labels[member.userID] ?? "" },
                                    set: { labels[member.userID] = $0 }
                                ))
                                .textFieldStyle(.roundedBorder)
                            }
                        }
                    }
                    Button {
                        let values = kind == .ladder
                            ? participants.map {
                                (labels[$0.userID] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                            }
                            : nil
                        actions.onCreateDraw(kind, participants.map(\.userID), values)
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
        .task(id: room?.id) {
            // Initialize once per room so later presence updates do not
            // overwrite participant choices the user has made manually.
            selectedIDs = DrawParticipantSelectionPolicy.initialIDs(in: room)
            labels = [:]
            actions.onRefreshDraws()
        }
    }
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
