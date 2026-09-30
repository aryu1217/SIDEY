import AppKit
import SwiftUI

struct OverlayHistoryView: View {
    @Bindable var model: AppModel
    @Bindable var history: MessageHistoryStore
    let onClose: () -> Void
    var onSend: (String) -> Void = { _ in }
    var onTypingChanged: (Bool) -> Void = { _ in }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            header

            Group {
                switch history.initialState {
                case .idle, .loading:
                    loadingView
                case .failed(let message):
                    initialFailureView(message: message)
                case .loaded where entries.isEmpty:
                    emptyView
                case .loaded:
                    messageList
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            composer
        }
        .padding(18)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor).opacity(0.72))
        .onChange(of: model.realtimeActiveRoomID, initial: true) { _, roomID in
            onTypingChanged(false)
            history.roomDidChange(to: roomID)
        }
    }

    private var header: some View {
        Text(L10n.format(
            "history.retention_notice",
            Int64(ProductLimits.messageRetentionDays)
        ))
        .font(.caption)
        .foregroundStyle(.secondary)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var loadingView: some View {
        VStack(spacing: 10) {
            ProgressView()
                .controlSize(.large)
            Text(L10n.text("history.loading"))
                .font(.callout)
                .foregroundStyle(.secondary)
        }
    }

    private func initialFailureView(message: String) -> some View {
        ContentUnavailableView {
            Label(
                L10n.text("history.error.load_failed"),
                systemImage: "exclamationmark.arrow.triangle.2.circlepath"
            )
        } description: {
            Text(message)
        } actions: {
            Button(L10n.text("common.retry")) { history.retryInitial() }
                .buttonStyle(.glass)
        }
    }

    private var emptyView: some View {
        ContentUnavailableView(
            L10n.text("history.empty.title"),
            systemImage: "bubble.left.and.bubble.right",
            description: Text(L10n.text("history.empty.description"))
        )
    }

    private var composer: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let error = model.historySendError {
                Text(error).font(.caption).foregroundStyle(.red)
            }
            HStack(spacing: 10) {
                ZStack(alignment: .leading) {
                    if model.draft.isEmpty {
                        Text(L10n.text("message.composer.placeholder"))
                            .foregroundStyle(.tertiary)
                    }
                    NativeMessageField(
                        text: $model.draft,
                        onInputActivity: {},
                        onSubmit: send,
                        onCancel: onClose,
                        onTextEdited: onTypingChanged,
                        onFocusLost: { onTypingChanged(false) }
                    )
                }
                .frame(height: 44)
                Button(action: send) {
                    Image(systemName: "arrow.up")
                        .font(.headline.weight(.semibold))
                        .frame(width: 24, height: 24)
                }
                .buttonStyle(.glassProminent)
                .buttonBorderShape(.circle)
                .disabled(!model.canSubmitDraft)
                .accessibilityLabel(L10n.text("message.send.accessibility"))
            }
            .padding(.horizontal, 10)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        }
    }

    private func send() {
        guard let body = model.acceptDraft() else { return }
        onTypingChanged(false)
        onSend(body)
    }

    private var messageList: some View {
        HistoryTimeline(
            items: entries.map { HistoryTimelineItem(entry: $0, participant: participant(for: $0.senderID)) },
            currentUserID: model.currentUserID,
            olderState: history.olderState,
            header: AnyView(paginationFooter),
            onLoadOlder: { history.loadNextPage() }
        )
        .id(history.roomID)
    }

    @ViewBuilder
    private var paginationFooter: some View {
        switch history.olderState {
        case .idle:
            Color.clear
                .frame(height: 2)

                .accessibilityHidden(true)
        case .loading:
            HStack(spacing: 8) {
                ProgressView()
                    .controlSize(.small)
                Text(L10n.text("history.load_more.loading"))
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 8)
        case .failed(let message):
            VStack(spacing: 6) {
                Text(L10n.text("history.load_more.failed"))
                    .font(.caption.weight(.semibold))
                Text(message)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                Button(L10n.text("common.retry")) { history.retryNextPage() }
                    .controlSize(.small)
                    .buttonStyle(.glass)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 8)
        case .exhausted:
            Text(L10n.format(
                "history.older.exhausted",
                Int64(ProductLimits.messageRetentionDays)
            ))
                .font(.caption2)
                .foregroundStyle(.tertiary)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 8)
        }
    }

    private var entries: [MessageLedgerEntry] {
        guard let roomID = history.roomID else { return [] }
        return MessageHistoryMerge.entries(
            pagedMessages: history.messages,
            ledger: model.messageLedger,
            outbox: model.messageOutbox,
            roomID: roomID
        )
    }

    private func participant(for senderID: UUID) -> MessageHistoryParticipant {
        let room = history.roomID.flatMap { roomID in
            model.rooms.first(where: { $0.id == roomID })
        }
        return MessageHistoryParticipantResolver.resolve(
            senderID: senderID,
            in: room,
            currentUserID: model.currentUserID
        )
    }
}

struct HistoryMessageCard: View {
    let entry: MessageLedgerEntry
    let participant: MessageHistoryParticipant

    private var bubbleTheme: PixelBubbleTheme { PixelBubbleTheme.resolve(entry.bubbleStyleID) }
    private var bubbleBorder: Color {
        // The default bubble stays white in both appearances; give it a visible
        // outline against the light history background rather than a glass edge.
        bubbleTheme.id == nil
            ? Color(red: 0.55, green: 0.57, blue: 0.61)
            : Color(nsColor: bubbleTheme.textColor).opacity(0.22)
    }

    var body: some View {
        HStack(alignment: .top, spacing: 4) {
            if participant.isCurrentUser { Spacer(minLength: 24) }
            if !participant.isCurrentUser { avatar }
            VStack(alignment: participant.isCurrentUser ? .trailing : .leading, spacing: 4) {
                messageBubble
                Text(entry.createdAt.formatted(date: .abbreviated, time: .shortened))
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            .padding(.top, 16)
            .frame(maxWidth: 440, alignment: participant.isCurrentUser ? .trailing : .leading)
            if participant.isCurrentUser { avatar }
            if !participant.isCurrentUser { Spacer(minLength: 24) }
        }
        .frame(maxWidth: .infinity, alignment: participant.isCurrentUser ? .trailing : .leading)
        .accessibilityElement(children: .combine)
    }

    private var avatar: some View {
        VStack(spacing: 3) {
            Text(participant.nickname)
                .font(.system(.caption2, design: .rounded, weight: .semibold))
                .lineLimit(1)
                .truncationMode(.middle)
                .help(participant.nickname)
            Image(nsImage: PixelCharacterPreviewImage.image(
                for: PixelCharacterCatalog.definition(for: participant.characterID)
            ))
            .interpolation(.none)
            .resizable()
            .frame(width: 48, height: 48)
            .accessibilityHidden(true)
        }
        .frame(width: 72)
    }

    private var messageBubble: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(entry.body)
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
            deliveryStatus
        }
        .foregroundStyle(Color(nsColor: bubbleTheme.textColor))
        .padding(.top, 10)
        .padding(.bottom, 10)
        .padding(.horizontal, 12)
        .padding(participant.isCurrentUser ? .trailing : .leading, 8)
        .frame(minWidth: bubbleTheme.decorationAssetName == nil ? 0 : 40,
               minHeight: 40, alignment: .leading)
        .background {
            HistoryMessageBubbleShape(tailOnRight: participant.isCurrentUser)
                .fill(Color(nsColor: bubbleTheme.backgroundColor.withAlphaComponent(1)))
        }
        .overlay {
            HistoryMessageBubbleShape(tailOnRight: participant.isCurrentUser)
                .stroke(bubbleBorder, lineWidth: 1)
                .allowsHitTesting(false)
        }
        .overlay(alignment: participant.isCurrentUser ? .topLeading : .topTrailing) {
            if let image = HistoryBubbleDecoration.image(for: bubbleTheme) {
                Image(nsImage: image)
                    .interpolation(.none)
                    .resizable()
                    .frame(width: 16, height: 16)
                    .offset(x: participant.isCurrentUser ? -8 : 8, y: -8)
                    .allowsHitTesting(false)
                    .accessibilityHidden(true)
            }
        }
    }

    @ViewBuilder
    private var deliveryStatus: some View {
        switch entry.state {
        case .pending:
            Label(L10n.text("message.delivery.pending"), systemImage: "clock")
                .font(.caption2)
                .foregroundStyle(Color(nsColor: bubbleTheme.textColor).opacity(0.75))
        case .failed:
            Label(
                L10n.text("message.delivery.failed"),
                systemImage: "exclamationmark.triangle.fill"
            )
                .font(.caption2)
                .foregroundStyle(bubbleTheme.id == "bubble_starry_cat" ? Color.yellow : Color.red)
        case .confirmed:
            EmptyView()
        }
    }
}

@MainActor
private enum HistoryBubbleDecoration {
    private static var images: [String: NSImage] = [:]

    static func image(for theme: PixelBubbleTheme) -> NSImage? {
        guard let name = theme.decorationAssetName else { return nil }
        if let image = images[name] { return image }
        guard let url = Bundle.main.url(forResource: name, withExtension: "png", subdirectory: "Bubbles")
                ?? Bundle.main.url(forResource: name, withExtension: "png"),
              let image = NSImage(contentsOf: url) else { return nil }
        images[name] = image
        return image
    }
}

private struct HistoryMessageBubbleShape: Shape {
    let tailOnRight: Bool

    func path(in rect: CGRect) -> Path {
        let tailWidth: CGFloat = 8
        let radius = min(9, rect.height / 2)
        let left = rect.minX + tailWidth
        let right = rect.maxX
        let top = rect.minY
        let bottom = rect.maxY
        let tailCenter = min(top + 24, bottom - radius - 5)
        var path = Path()
        path.move(to: CGPoint(x: left + radius, y: top))
        path.addLine(to: CGPoint(x: right - radius, y: top))
        path.addQuadCurve(to: CGPoint(x: right, y: top + radius),
                          control: CGPoint(x: right, y: top))
        path.addLine(to: CGPoint(x: right, y: bottom - radius))
        path.addQuadCurve(to: CGPoint(x: right - radius, y: bottom),
                          control: CGPoint(x: right, y: bottom))
        path.addLine(to: CGPoint(x: left + radius, y: bottom))
        path.addQuadCurve(to: CGPoint(x: left, y: bottom - radius),
                          control: CGPoint(x: left, y: bottom))
        path.addLine(to: CGPoint(x: left, y: tailCenter + 5))
        path.addLine(to: CGPoint(x: rect.minX, y: tailCenter))
        path.addLine(to: CGPoint(x: left, y: tailCenter - 5))
        path.addLine(to: CGPoint(x: left, y: top + radius))
        path.addQuadCurve(to: CGPoint(x: left + radius, y: top),
                          control: CGPoint(x: left, y: top))
        path.closeSubpath()
        if tailOnRight {
            return path.applying(CGAffineTransform(
                a: -1, b: 0, c: 0, d: 1, tx: rect.minX + rect.maxX, ty: 0
            ))
        }
        return path
    }
}

struct HistoryTimelineItem: Equatable {
    let entry: MessageLedgerEntry
    let participant: MessageHistoryParticipant
}

private struct HistoryTimeline: NSViewRepresentable {
    let items: [HistoryTimelineItem]
    let currentUserID: UUID?
    let olderState: MessageHistoryOlderState
    let header: AnyView
    let onLoadOlder: () -> Void

    func makeNSView(context: Context) -> HistoryTimelineScrollView { HistoryTimelineScrollView() }

    func updateNSView(_ view: HistoryTimelineScrollView, context: Context) {
        view.update(items: items, currentUserID: currentUserID, olderState: olderState,
                    header: header, onLoadOlder: onLoadOlder)
    }
}

private final class HistoryTimelineDocumentView: NSView {
    override var isFlipped: Bool { true }
}

/// Owns scrolling in document coordinates so inserting older rows preserves even
/// a partially visible card, rather than snapping the reader to its top edge.
@MainActor
final class HistoryTimelineScrollView: NSScrollView {
    private let document = HistoryTimelineDocumentView()
    private let headerView = NSHostingView(rootView: AnyView(EmptyView()))
    private struct RowMeasurement {
        let item: HistoryTimelineItem
        let width: CGFloat
        let height: CGFloat
    }
    private var rowViews: [UUID: NSHostingView<AnyView>] = [:]
    private var rowMeasurements: [UUID: RowMeasurement] = [:]
    private var items: [HistoryTimelineItem] = []
    private var olderState: MessageHistoryOlderState = .exhausted
    private var header = AnyView(EmptyView())
    private var onLoadOlder: () -> Void = {}
    private var laidOutWidth: CGFloat = 0
    private var isUpdating = false
    private var paginationScheduled = false
    private var needsInitialScroll = true

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        drawsBackground = false
        hasVerticalScroller = true
        hasHorizontalScroller = false
        autohidesScrollers = true
        borderType = .noBorder
        documentView = document
        document.addSubview(headerView)
        contentView.postsBoundsChangedNotifications = true
        NotificationCenter.default.addObserver(self, selector: #selector(viewportChanged),
            name: NSView.boundsDidChangeNotification, object: contentView)
    }

    convenience init() { self.init(frame: .zero) }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    deinit { NotificationCenter.default.removeObserver(self) }

    func update(items: [HistoryTimelineItem], currentUserID: UUID?,
                olderState: MessageHistoryOlderState, header: AnyView,
                onLoadOlder: @escaping () -> Void) {
        self.onLoadOlder = onLoadOlder
        self.header = header
        guard self.items != items || self.olderState != olderState || needsInitialScroll else { return }
        let anchor = readingAnchor()
        let wasAtBottom = isAtBottom
        let previousIDs = Set(self.items.map { $0.entry.id })
        let latest = self.items.last?.entry
        let hasNewOwnMessage = items.contains { item in
            guard item.entry.senderID == currentUserID,
                  !previousIDs.contains(item.entry.id) else { return false }
            // Local staging uses the device clock; a clock behind the server
            // must not suppress the explicit local-send scroll request.
            if item.entry.state == .pending { return true }
            guard let latest else { return true }
            return item.entry.createdAt > latest.createdAt
                || (item.entry.createdAt == latest.createdAt && item.entry.id.uuidString > latest.id.uuidString)
        }
        self.items = items
        self.olderState = olderState
        let retainedIDs = Set(items.map { $0.entry.id })
        for id in Array(rowViews.keys) where !retainedIDs.contains(id) {
            rowViews.removeValue(forKey: id)?.removeFromSuperview()
            rowMeasurements.removeValue(forKey: id)
        }
        for item in items where rowViews[item.entry.id] == nil {
            let view = NSHostingView(rootView: AnyView(EmptyView()))
            rowViews[item.entry.id] = view
            document.addSubview(view)
        }
        layoutRows(anchor: anchor, followBottom: wasAtBottom || hasNewOwnMessage || needsInitialScroll)
    }

    override func layout() {
        super.layout()
        guard !isUpdating, contentSize.width > 0,
              abs(laidOutWidth - contentSize.width) > 0.5 || needsInitialScroll else { return }
        layoutRows(anchor: readingAnchor(), followBottom: isAtBottom || needsInitialScroll)
    }

    var isAtBottom: Bool {
        document.bounds.height - contentView.bounds.maxY <= 4
    }

    /// Exposed as geometry, also used by deterministic scroll regression tests.
    func frameForMessage(_ id: UUID) -> CGRect? { rowViews[id]?.frame }

    private func readingAnchor() -> (id: UUID, offset: CGFloat)? {
        let top = contentView.bounds.minY
        for item in items {
            if let row = rowViews[item.entry.id], row.frame.maxY > top {
                return (item.entry.id, top - row.frame.minY)
            }
        }
        return nil
    }

    private func layoutRows(anchor: (id: UUID, offset: CGFloat)?, followBottom: Bool) {
        guard !isUpdating, contentSize.width > 0, contentSize.height > 0 else { return }
        isUpdating = true
        defer { isUpdating = false }
        let width = contentSize.width
        laidOutWidth = width
        headerView.rootView = AnyView(header.frame(width: width).fixedSize(horizontal: false, vertical: true))
        headerView.layoutSubtreeIfNeeded()
        let headerHeight = max(2, headerView.fittingSize.height)
        headerView.frame = CGRect(x: 0, y: 0, width: width, height: headerHeight)
        var y = headerHeight + 10
        for item in items {
            guard let view = rowViews[item.entry.id] else { continue }
            let height: CGFloat
            if let measured = rowMeasurements[item.entry.id],
               measured.item == item, abs(measured.width - width) <= 0.5 {
                height = measured.height
            } else {
                view.rootView = AnyView(HistoryMessageCard(entry: item.entry, participant: item.participant)
                    .frame(width: width).fixedSize(horizontal: false, vertical: true))
                view.layoutSubtreeIfNeeded()
                height = max(1, view.fittingSize.height)
                rowMeasurements[item.entry.id] = RowMeasurement(item: item, width: width, height: height)
            }
            let frame = CGRect(x: 0, y: y, width: width, height: height)
            if view.frame != frame { view.frame = frame }
            y += height + 10
        }
        document.frame = CGRect(x: 0, y: 0, width: width, height: max(contentSize.height, y))
        let maximum = max(0, document.bounds.height - contentView.bounds.height)
        let targetY: CGFloat
        if followBottom {
            targetY = maximum
        } else if let anchor, let row = rowViews[anchor.id] {
            targetY = row.frame.minY + anchor.offset
        } else {
            targetY = contentView.bounds.minY
        }
        contentView.scroll(to: NSPoint(x: 0, y: min(maximum, max(0, targetY))))
        reflectScrolledClipView(contentView)
        needsInitialScroll = false
        schedulePaginationIfNeeded()
    }

    @objc private func viewportChanged() {
        guard !isUpdating else { return }
        schedulePaginationIfNeeded()
    }

    private func schedulePaginationIfNeeded() {
        guard !paginationScheduled, !needsInitialScroll,
              contentView.bounds.minY <= 40, olderState == .idle else { return }
        paginationScheduled = true
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.paginationScheduled = false
            guard self.contentView.bounds.minY <= 40, self.olderState == .idle else { return }
            self.onLoadOlder()
        }
    }
}
