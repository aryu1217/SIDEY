import AppKit

enum StatusItemIconProvider {
    static let regularAssetName = "SideyMenuIcon"
    static let unreadAssetName = "SideyMenuIconUnread"

    static func image(hasUnread: Bool) -> NSImage? {
        let description = hasUnread
            ? L10n.format("status.icon.unread.accessibility", AppPresentation.displayName)
            : AppPresentation.displayName
        let assetName = hasUnread ? unreadAssetName : regularAssetName
        if let asset = NSImage(named: NSImage.Name(assetName))?.copy() as? NSImage {
            asset.isTemplate = true
            asset.accessibilityDescription = description
            return asset
        }
        let fallback = NSImage(
            systemSymbolName: "pawprint.fill",
            accessibilityDescription: description
        )
        fallback?.isTemplate = true
        return fallback
    }
}

@MainActor
final class StatusItemController: NSObject, NSMenuDelegate {
    private let onToggleOverlay: () -> Void
    private let onFocusMessage: () -> Void
    private let onSelectRoom: (UUID) -> Void
    private let onToggleQuietMode: () -> Void
    private let onOpenHistory: () -> Void
    private let onOpenStore: () -> Void
    private let onToggleLaunchAtLogin: () -> Void
    private let onOpenGroupSettings: () -> Void
    private let onOpenSettings: () -> Void
    private let onQuit: () -> Void
    private var statusItem: NSStatusItem?
    private var overlayVisible = true
    private var rooms: [Room] = []
    private var activeRoomID: UUID?
    private var unreadCounts: [UUID: Int] = [:]
    private var quietModeEnabled = false
    private var launchAtLogin = false
    private var globalShortcuts = GlobalShortcutConfiguration.defaults
    private var globalShortcutStatuses: [GlobalShortcutAction: GlobalShortcutStatus] = [:]

    init(
        onToggleOverlay: @escaping () -> Void,
        onFocusMessage: @escaping () -> Void = {},
        onSelectRoom: @escaping (UUID) -> Void = { _ in },
        onToggleQuietMode: @escaping () -> Void = {},
        onOpenHistory: @escaping () -> Void = {},
        onOpenStore: @escaping () -> Void = {},
        onToggleLaunchAtLogin: @escaping () -> Void = {},
        onOpenGroupSettings: @escaping () -> Void = {},
        onOpenSettings: @escaping () -> Void,
        onQuit: @escaping () -> Void
    ) {
        self.onToggleOverlay = onToggleOverlay
        self.onFocusMessage = onFocusMessage
        self.onSelectRoom = onSelectRoom
        self.onToggleQuietMode = onToggleQuietMode
        self.onOpenHistory = onOpenHistory
        self.onOpenStore = onOpenStore
        self.onToggleLaunchAtLogin = onToggleLaunchAtLogin
        self.onOpenGroupSettings = onOpenGroupSettings
        self.onOpenSettings = onOpenSettings
        self.onQuit = onQuit
    }

    func install() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = StatusItemIconProvider.image(hasUnread: false)
        item.button?.toolTip = AppPresentation.displayName
        item.menu = makeMenu()
        statusItem = item
    }

    func update(
        overlayVisible: Bool,
        rooms: [Room] = [],
        activeRoomID: UUID? = nil,
        unreadCounts: [UUID: Int] = [:],
        quietModeEnabled: Bool = false,
        launchAtLogin: Bool = false,
        globalShortcuts: GlobalShortcutConfiguration = .defaults,
        globalShortcutStatuses: [GlobalShortcutAction: GlobalShortcutStatus] = [:]
    ) {
        self.overlayVisible = overlayVisible
        self.rooms = rooms
        self.activeRoomID = activeRoomID
        self.unreadCounts = unreadCounts
        self.quietModeEnabled = quietModeEnabled
        self.launchAtLogin = launchAtLogin
        self.globalShortcuts = globalShortcuts
        self.globalShortcutStatuses = globalShortcutStatuses
        updateStatusIcon()
        statusItem?.menu = makeMenu()
    }

    func makeMenu() -> NSMenu {
        let menu = NSMenu(title: AppPresentation.displayName)
        menu.delegate = self
        let overlay = NSMenuItem(
            title: L10n.text(
                overlayVisible ? "status.menu.overlay.hide" : "status.menu.overlay.show"
            ),
            action: #selector(toggleOverlay),
            keyEquivalent: ""
        )
        annotateShortcut(.toggleOverlay, on: overlay)
        overlay.target = self
        menu.addItem(overlay)

        let message = NSMenuItem(
            title: L10n.text("status.menu.compose"),
            action: #selector(focusMessage),
            keyEquivalent: ""
        )
        annotateShortcut(.toggleComposer, on: message)
        message.target = self
        message.isEnabled = !rooms.isEmpty
        menu.addItem(message)

        menu.addItem(.separator())

        let groups = NSMenuItem(
            title: L10n.text("status.menu.active_group"),
            action: nil,
            keyEquivalent: ""
        )
        groups.submenu = makeRoomsMenu()
        groups.isEnabled = !rooms.isEmpty
        menu.addItem(groups)

        let quiet = NSMenuItem(
            title: L10n.text("status.menu.quiet_mode"),
            action: #selector(toggleQuietMode),
            keyEquivalent: ""
        )
        annotateShortcut(.toggleQuietMode, on: quiet)
        quiet.target = self
        quiet.state = quietModeEnabled ? .on : .off
        menu.addItem(quiet)

        let history = NSMenuItem(
            title: L10n.text("status.menu.history"),
            action: #selector(openHistory),
            keyEquivalent: ""
        )
        annotateShortcut(.openHistory, on: history)
        history.target = self
        history.isEnabled = !rooms.isEmpty
        menu.addItem(history)

        let store = NSMenuItem(
            title: L10n.text("status.menu.store"),
            action: #selector(openStore),
            keyEquivalent: ""
        )
        store.target = self
        menu.addItem(store)

        let groupSettings = NSMenuItem(
            title: L10n.text("status.menu.group_settings"),
            action: #selector(openGroupSettings),
            keyEquivalent: ""
        )
        groupSettings.target = self
        menu.addItem(groupSettings)

        let login = NSMenuItem(
            title: L10n.text("settings.startup.title"),
            action: #selector(toggleLaunchAtLogin),
            keyEquivalent: ""
        )
        login.target = self
        login.state = launchAtLogin ? .on : .off
        menu.addItem(login)
        menu.addItem(.separator())

        let settings = NSMenuItem(
            title: L10n.text("status.menu.settings"),
            action: #selector(openSettings),
            keyEquivalent: ","
        )
        settings.target = self
        menu.addItem(settings)
        menu.addItem(.separator())

        let quit = NSMenuItem(
            title: L10n.format("status.menu.quit", AppPresentation.displayName),
            action: #selector(quit),
            keyEquivalent: "q"
        )
        quit.target = self
        menu.addItem(quit)
        return menu
    }

    private func annotateShortcut(_ action: GlobalShortcutAction, on item: NSMenuItem) {
        // Carbon owns dispatch. A menu key equivalent would create a second execution path.
        let status = globalShortcutStatuses[action]
        let shortcut = globalShortcuts[action]
        let annotation = status?.menuAnnotation.map { " · \($0)" } ?? ""
        let suffix = shortcut.displayShortcut + annotation
        let title = item.title
        let paragraph = NSMutableParagraphStyle()
        paragraph.tabStops = [NSTextTab(textAlignment: .right, location: 220)]
        let attributedTitle = NSMutableAttributedString(string: "\(title)\t\(suffix)")
        attributedTitle.addAttribute(
            .paragraphStyle,
            value: paragraph,
            range: NSRange(location: 0, length: attributedTitle.length)
        )
        attributedTitle.addAttribute(
            .foregroundColor,
            value: NSColor.secondaryLabelColor,
            range: NSRange(location: (title as NSString).length + 1, length: (suffix as NSString).length)
        )
        item.attributedTitle = attributedTitle
        item.title = title
        item.toolTip = status?.notice ?? shortcut.descriptiveShortcut
    }

    private func makeRoomsMenu() -> NSMenu {
        let menu = NSMenu(title: L10n.text("status.menu.active_group"))
        if rooms.isEmpty {
            let empty = NSMenuItem(
                title: L10n.text("status.menu.no_connected_groups"),
                action: nil,
                keyEquivalent: ""
            )
            empty.isEnabled = false
            menu.addItem(empty)
            return menu
        }
        for room in rooms {
            let unread = unreadCounts[room.id, default: 0]
            let localizedCount = NumberFormatter.localizedString(
                from: NSNumber(value: unread),
                number: .decimal
            )
            let suffix = unread > 0
                ? L10n.format("status.room.unread_count", localizedCount)
                : ""
            let item = NSMenuItem(
                title: room.name + suffix,
                action: #selector(selectRoom(_:)),
                keyEquivalent: ""
            )
            item.target = self
            item.representedObject = room.id.uuidString
            item.state = room.id == activeRoomID ? .on : .off
            menu.addItem(item)
        }
        return menu
    }

    private func updateStatusIcon() {
        let hasUnread = unreadCounts.values.contains(where: { $0 > 0 })
        statusItem?.button?.image = StatusItemIconProvider.image(hasUnread: hasUnread)
        statusItem?.button?.toolTip = hasUnread
            ? L10n.format("status.tooltip.unread", AppPresentation.displayName)
            : AppPresentation.displayName
    }

    @objc private func toggleOverlay() { onToggleOverlay() }
    @objc private func focusMessage() { onFocusMessage() }
    @objc private func selectRoom(_ sender: NSMenuItem) {
        guard let rawID = sender.representedObject as? String, let roomID = UUID(uuidString: rawID) else { return }
        onSelectRoom(roomID)
    }
    @objc private func toggleQuietMode() { onToggleQuietMode() }
    @objc private func openHistory() { onOpenHistory() }
    @objc private func openStore() { onOpenStore() }
    @objc private func toggleLaunchAtLogin() { onToggleLaunchAtLogin() }
    @objc private func openGroupSettings() { onOpenGroupSettings() }
    @objc private func openSettings() { onOpenSettings() }
    @objc private func quit() { onQuit() }
}
