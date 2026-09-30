import SwiftUI

struct ConnectionControls: View {
    let state: BackendConnectionState
    let onReconnect: () -> Void

    var body: some View {
        ConnectionBadge(state: state, onReconnect: onReconnect)
    }
}

struct ConnectionBadge: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let state: BackendConnectionState
    let onReconnect: () -> Void

    var body: some View {
        HStack(spacing: 8) {
            Circle().fill(color).frame(width: 8, height: 8)
            Text(localizedLabel)
                .font(.caption.weight(.medium))
                .lineLimit(1)
            Spacer()
        }
        .padding(.trailing, canRetry ? 26 : 0)
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
        .clipShape(Capsule())
        .glassEffect(in: Capsule())
        .overlay(alignment: .trailing) {
            if canRetry {
                Button(action: onReconnect) {
                    Image(systemName: "arrow.clockwise")
                        .font(.caption.weight(.semibold))
                        .frame(width: 22, height: 22)
                        .contentShape(Circle())
                }
                .buttonStyle(.plain)
                .help("connection.retry.action")
                .accessibilityLabel("connection.retry.action")
                .padding(.trailing, 12)
            }
        }
        .overlay {
            if state == .connecting {
                ConnectionProgressBorder(reduceMotion: reduceMotion)
                    .allowsHitTesting(false)
                    .accessibilityHidden(true)
            }
        }
    }

    private var canRetry: Bool {
        if case .failed = state { return true }
        return false
    }

    private var color: Color {
        switch state {
        case .online: .green
        case .connecting: .orange
        case .idle: .gray
        case .failed: .red
        }
    }

    private var localizedLabel: LocalizedStringResource {
        switch state {
        case .idle: "connection.state.idle"
        case .connecting: "connection.state.connecting"
        case .online: "connection.state.online"
        case .failed: "connection.state.failed"
        }
    }
}

struct ConnectionProgressBorder: View {
    let reduceMotion: Bool
    @State private var isVisible = false

    var body: some View {
        Group {
            if reduceMotion {
                Capsule().strokeBorder(.orange.opacity(0.65), lineWidth: 1.5)
            } else {
                TimelineView(.animation(
                    minimumInterval: 1.0 / 30,
                    // Settings uses NSWindow + NSHostingView, not a SwiftUI
                    // Scene. Its scenePhase is not a window-visibility signal.
                    paused: !isVisible
                )) { context in
                    let phase = context.date.timeIntervalSinceReferenceDate
                        .truncatingRemainder(dividingBy: 1.6) / 1.6
                    Capsule()
                        .strokeBorder(.orange.opacity(0.2), lineWidth: 1.5)
                        .overlay {
                            Capsule().strokeBorder(
                                AngularGradient(
                                    stops: [
                                        .init(color: .clear, location: 0),
                                        .init(color: .clear, location: 0.55),
                                        .init(color: .orange.opacity(0.3), location: 0.75),
                                        .init(color: .orange, location: 0.95),
                                        .init(color: .clear, location: 1)
                                    ],
                                    center: .center,
                                    angle: .degrees(phase * 360)
                                ),
                                lineWidth: 1.5
                            )
                        }
                }
            }
        }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
    }
}

struct ErrorBanner: View {
    let message: String
    let onDismiss: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
            Text(verbatim: message).lineLimit(3)
            Spacer()
            Button(action: onDismiss) { Image(systemName: "xmark") }
                .buttonStyle(.plain)
                .accessibilityLabel("common.close")
        }
        .padding(16)
        .frame(maxWidth: 620)
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .glassEffect(in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

struct SuccessBanner: View {
    let message: String
    let onDismiss: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
            Text(verbatim: message).lineLimit(2)
            Spacer()
            Button(action: onDismiss) { Image(systemName: "xmark") }
                .buttonStyle(.plain)
                .accessibilityLabel("common.close")
        }
        .padding(16)
        .frame(maxWidth: 620)
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .glassEffect(in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

struct SettingsSection<Content: View>: View {
    let title: LocalizedStringResource
    private let subtitle: SettingsRowDescription
    let systemImage: String?
    let content: Content

    init(
        title: LocalizedStringResource,
        subtitle: LocalizedStringResource,
        systemImage: String? = nil,
        @ViewBuilder content: () -> Content
    ) {
        self.title = title
        self.subtitle = .localized(subtitle)
        self.systemImage = systemImage
        self.content = content()
    }

    init(
        title: LocalizedStringResource,
        verbatimSubtitle: String,
        systemImage: String? = nil,
        @ViewBuilder content: () -> Content
    ) {
        self.title = title
        self.subtitle = .verbatim(verbatimSubtitle)
        self.systemImage = systemImage
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .top, spacing: 10) {
                if let systemImage {
                    Image(systemName: systemImage)
                        .font(.title3.weight(.semibold))
                        .foregroundStyle(.tint)
                        .frame(width: 24, height: 24)
                        .accessibilityHidden(true)
                }
                VStack(alignment: .leading, spacing: 5) {
                    Text(title)
                        .font(.title2.bold())
                    subtitle.text
                        .font(.body)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            VStack(alignment: .leading, spacing: 18) { content }
                .padding(24)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(
                    Color.primary.opacity(0.025),
                    in: RoundedRectangle(cornerRadius: 16, style: .continuous)
                )
                .overlay {
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .stroke(.primary.opacity(0.05), lineWidth: 1)
                }
                .shadow(color: .black.opacity(0.025), radius: 12, y: 4)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct SettingsToggleRow: View {
    let title: LocalizedStringResource
    let description: LocalizedStringResource
    @Binding var isOn: Bool

    var body: some View {
        HStack(alignment: .center, spacing: 24) {
            SettingsRowLabel(title: title, description: .localized(description))
            Spacer(minLength: 16)
            Toggle(isOn: $isOn) {
                Text(title)
            }
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.large)
                .tint(.accentColor)
                .frame(width: 240, alignment: .trailing)
        }
        .frame(maxWidth: .infinity)
        .contentShape(Rectangle())
    }
}

struct SettingsControlRow<Control: View>: View {
    let title: LocalizedStringResource
    private let description: SettingsRowDescription
    let control: Control

    init(
        title: LocalizedStringResource,
        description: LocalizedStringResource,
        @ViewBuilder control: () -> Control
    ) {
        self.title = title
        self.description = .localized(description)
        self.control = control()
    }

    init(
        title: LocalizedStringResource,
        verbatimDescription: String,
        @ViewBuilder control: () -> Control
    ) {
        self.title = title
        self.description = .verbatim(verbatimDescription)
        self.control = control()
    }

    var body: some View {
        HStack(alignment: .center, spacing: 24) {
            SettingsRowLabel(title: title, description: description)
            Spacer(minLength: 16)
            control
                .frame(width: 240, alignment: .trailing)
        }
        .frame(maxWidth: .infinity)
    }
}

private struct SettingsRowLabel: View {
    let title: LocalizedStringResource
    let description: SettingsRowDescription

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title)
                .font(.headline)
            description.text
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private enum SettingsRowDescription {
    case localized(LocalizedStringResource)
    case verbatim(String)

    @ViewBuilder var text: some View {
        switch self {
        case .localized(let resource):
            Text(resource)
        case .verbatim(let value):
            Text(verbatim: value)
        }
    }
}
