import AppKit
import SwiftUI
import XCTest
@testable import SIDEY

@MainActor
final class SettingsInteractionTests: XCTestCase {
    func testConnectionBorderMovesWithoutAnActiveSwiftUIScene() async throws {
        let frames = try await connectionBorderFrames(reduceMotion: false)
        XCTAssertGreaterThan(Set(frames.suffix(6)).count, 2,
            "An AppKit-hosted connecting border must animate without an active SwiftUI Scene")
    }

    func testConnectionBorderRemainsStillWhenReduceMotionIsEnabled() async throws {
        let frames = try await connectionBorderFrames(reduceMotion: true)
        XCTAssertEqual(Set(frames.suffix(6)).count, 1)
    }

    private func connectionBorderFrames(reduceMotion: Bool) async throws -> [Data] {
        let root = ConnectionProgressBorder(reduceMotion: reduceMotion)
            .environment(\.scenePhase, .background)
            .frame(width: 220, height: 34)
            .padding(4)
            .background(Color.black)
        let hostingView = NSHostingView(rootView: root)
        hostingView.frame = CGRect(x: 0, y: 0, width: 228, height: 42)
        let window = NSWindow(contentRect: hostingView.frame,
            styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = hostingView
        window.orderFrontRegardless()
        defer { window.close() }

        var frames: [Data] = []
        // Sample a full 1.6-second rotation, including time for initial layout.
        // The paused implementation must yield identical rendered frames.
        for _ in 0..<10 {
            try await Task.sleep(for: .milliseconds(180))
            hostingView.layoutSubtreeIfNeeded()
            hostingView.displayIfNeeded()
            let bitmap = try XCTUnwrap(
                hostingView.bitmapImageRepForCachingDisplay(in: hostingView.bounds))
            hostingView.cacheDisplay(in: hostingView.bounds, to: bitmap)
            frames.append(try XCTUnwrap(bitmap.representation(using: .png, properties: [:])))
        }
        return frames
    }

    func testLegalLinksUseCanonicalPublicWebsiteRoutes() {
        XCTAssertEqual(
            AppSettingsLegalLinks.localizedURL(
                page: "privacy",
                preferredLocalization: "ko"
            ).absoluteString,
            "https://sidey-app.github.io/SIDEY/ko/privacy/"
        )
        XCTAssertEqual(
            AppSettingsLegalLinks.localizedURL(
                page: "terms",
                preferredLocalization: "ko"
            ).absoluteString,
            "https://sidey-app.github.io/SIDEY/ko/terms/"
        )
    }

    func testLegalLinksFollowTheSupportedAppLocalizationAndFallbackToEnglish() {
        let expectations = [
            ("ko", "https://sidey-app.github.io/SIDEY/ko/privacy/"),
            ("en-GB", "https://sidey-app.github.io/SIDEY/en/privacy/"),
            ("ja", "https://sidey-app.github.io/SIDEY/ja/privacy/"),
            ("zh-Hant", "https://sidey-app.github.io/SIDEY/zh-hant/privacy/"),
            ("zh-Hans", "https://sidey-app.github.io/SIDEY/en/privacy/"),
            ("fr", "https://sidey-app.github.io/SIDEY/en/privacy/")
        ]

        for (localization, expectedURL) in expectations {
            XCTAssertEqual(
                AppSettingsLegalLinks.localizedURL(
                    page: "privacy",
                    preferredLocalization: localization
                ).absoluteString,
                expectedURL
            )
        }
    }

    func testInviteCopySuccessIsVisibleForThreeSeconds() throws {
        var state = InviteCopyFeedbackState()

        let generation = try XCTUnwrap(state.recordResult(true))

        XCTAssertEqual(InviteCopyFeedbackState.confirmationDuration, .seconds(3))
        XCTAssertTrue(state.showsConfirmation)
        state.clear(generation: generation)
        XCTAssertFalse(state.showsConfirmation)
    }

    func testSuccessBannerExpiresAfterThreeSecondsAndNewSuccessInvalidatesOldTimer() throws {
        var state = SuccessFeedbackState()

        let firstGeneration = state.present("첫 성공")
        let secondGeneration = state.present("두 번째 성공")
        state.dismiss(generation: firstGeneration)

        XCTAssertEqual(SuccessFeedbackState.displayDuration, .seconds(3))
        XCTAssertEqual(state.message, "두 번째 성공")
        state.dismiss(generation: secondGeneration)
        XCTAssertNil(state.message)
    }

    func testInviteCopyFailureDoesNotShowConfirmation() {
        var state = InviteCopyFeedbackState()

        XCTAssertNil(state.recordResult(false))
        XCTAssertFalse(state.showsConfirmation)
    }

    func testProfileSaveCommitsMarkedHangulBeforeRunningAction() async throws {
        let window = NSWindow(
            contentRect: CGRect(x: 0, y: 0, width: 320, height: 120),
            styleMask: [.borderless],
            backing: .buffered,
            defer: false
        )
        let textView = NSTextView(frame: window.contentView!.bounds)
        window.contentView = textView
        XCTAssertTrue(window.makeFirstResponder(textView))
        textView.setMarkedText(
            "별",
            selectedRange: NSRange(location: 1, length: 0),
            replacementRange: NSRange(location: NSNotFound, length: 0)
        )
        XCTAssertTrue(textView.hasMarkedText())
        let actionRan = expectation(description: "프로필 저장 액션 실행")

        PendingTextInputCommitter.commitThen(in: window) {
            XCTAssertFalse(textView.hasMarkedText())
            XCTAssertEqual(textView.string, "별")
            actionRan.fulfill()
        }

        await fulfillment(of: [actionRan], timeout: 1)
    }

    func testRepeatedInviteCopyInvalidatesEarlierResetTimer() throws {
        var state = InviteCopyFeedbackState()

        let firstGeneration = try XCTUnwrap(state.recordResult(true))
        let secondGeneration = try XCTUnwrap(state.recordResult(true))
        state.clear(generation: firstGeneration)

        XCTAssertTrue(state.showsConfirmation)
        state.clear(generation: secondGeneration)
        XCTAssertFalse(state.showsConfirmation)
    }

    func testRemovingRoomRowCancelsInviteCopyFeedback() throws {
        var state = InviteCopyFeedbackState()
        let generation = try XCTUnwrap(state.recordResult(true))

        state.cancel()
        state.clear(generation: generation)

        XCTAssertFalse(state.showsConfirmation)
    }

    func testGroupsUseCompactTwoPersonSymbol() {
        XCTAssertEqual(SettingsPage.groups.systemImage, "person.2")
        XCTAssertNotNil(NSImage(systemSymbolName: SettingsPage.groups.systemImage, accessibilityDescription: nil))
    }

    func testGroupOperationUsesSpecificProgressLabelsAndMutationPolicy() {
        let roomID = UUID()

        XCTAssertEqual(GroupOperation.creating.createButtonTitle, L10n.text("group.create.in_progress"))
        XCTAssertEqual(GroupOperation.joining.joinButtonTitle, L10n.text("group.join.in_progress"))
        XCTAssertTrue(GroupOperation.switching(roomID).isSwitching(to: roomID))
        XCTAssertTrue(GroupOperation.switching(roomID).allowsRoomSelection)
        XCTAssertTrue(GroupOperation.switching(roomID).blocksMutations)
        XCTAssertFalse(GroupOperation.creating.allowsRoomSelection)
    }

    func testSwitchingGroupCardRendersInLightAndDarkModes() throws {
        let activeRoomID = UUID()
        let targetRoomID = UUID()
        let snapshotDirectory = URL(
            fileURLWithPath: "/private/tmp/sidey-group-snapshots",
            isDirectory: true
        )
        let writesSnapshots = FileManager.default.fileExists(atPath: snapshotDirectory.path)
        for scheme in [ColorScheme.light, .dark] {
            let data = try renderSettings(
                size: SettingsWindowController.settingsContentSize,
                colorScheme: scheme
            ) { model in
                model.activeSettingsPage = .groups
                model.preferences.activeRoomID = activeRoomID
                model.rooms = [
                    Self.room(id: activeRoomID, name: "현재 그룹"),
                    Self.room(id: targetRoomID, name: "전환 대상")
                ]
                model.groupOperation = .switching(targetRoomID)
            }
            XCTAssertGreaterThan(data.count, 10_000)
            if writesSnapshots {
                let mode = scheme == .light ? "light" : "dark"
                try data.write(to: snapshotDirectory.appending(path: "groups-switching-\(mode).png"))
            }
        }
    }

    func testTwelveMemberGroupRendersAtCapacity() throws {
        let roomID = UUID()
        let members = (0..<ProductLimits.maximumRoomMembers).map { index in
            RoomMember(
                userID: UUID(),
                nickname: "친구\(index + 1)",
                characterID: "pixel_hamster",
                presence: .online
            )
        }

        let data = try renderSettings(
            size: SettingsWindowController.settingsContentSize,
            colorScheme: .light
        ) { model in
            model.activeSettingsPage = .groups
            model.preferences.activeRoomID = roomID
            model.rooms = [Self.room(id: roomID, name: "가득 찬 그룹", members: members)]
        }

        XCTAssertEqual(members.count, 12)
        XCTAssertGreaterThan(data.count, 10_000)
    }

    func testSettingsRenderAtMinimumAndDefaultSizesInLightAndDarkModes() throws {
        let snapshotSentinel = "/private/tmp/sidey-render-settings-snapshots"
        let configuredOutput = ProcessInfo.processInfo.environment["SIDEY_SETTINGS_SNAPSHOT_DIR"]
        let outputDirectory = configuredOutput
            .map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? (FileManager.default.fileExists(atPath: snapshotSentinel)
                ? URL(fileURLWithPath: "/private/tmp/sidey-settings-alpha6", isDirectory: true)
                : nil)
        if let outputDirectory {
            try FileManager.default.createDirectory(
                at: outputDirectory,
                withIntermediateDirectories: true
            )
        }

        for (sizeName, size) in [
            ("minimum", CGSize(width: 860, height: 640)),
            ("default", SettingsWindowController.settingsContentSize)
        ] {
            for scheme in [ColorScheme.light, .dark] {
                let data = try renderSettings(size: size, colorScheme: scheme)
                XCTAssertGreaterThan(data.count, 10_000)
                if let outputDirectory {
                    let mode = scheme == .light ? "light" : "dark"
                    try data.write(to: outputDirectory.appending(path: "settings-\(sizeName)-\(mode).png"))

                    let placementData = try renderSettings(
                        size: size,
                        colorScheme: scheme,
                        scrollToBottom: true
                    )
                    XCTAssertGreaterThan(placementData.count, 10_000)
                    try placementData.write(
                        to: outputDirectory.appending(path: "settings-\(sizeName)-\(mode)-placement.png")
                    )
                }
            }
        }
    }

    func testTwoColumnStoreGridRendersLongAndIndependentCardStates() throws {
        let products = [
            CommerceProduct(
                id: "fixture_store_first",
                displayName: "첫 번째 테스트 캐릭터",
                description: "최소 크기에서도 설명이 자연스럽게 여러 줄로 이어지고 아래 버튼을 밀어내거나 잘라내지 않는지 확인하는 긴 상품 설명입니다.",
                characterID: "pixel_hamster",
                entitlementKey: "character:fixture_store_first",
                amountKRW: 990,
                currency: "KRW",
                taxInclusive: true
            ),
            CommerceProduct(
                id: "fixture_store_second",
                displayName: "두 번째 테스트 캐릭터",
                description: "두 번째 카드의 상태가 첫 번째 카드와 독립적으로 표시되는지 확인합니다.",
                characterID: "pixel_cat",
                entitlementKey: "character:fixture_store_second",
                amountKRW: 1_900,
                currency: "KRW",
                taxInclusive: true
            )
        ]
        let snapshotSentinel = "/private/tmp/sidey-store-snapshots"
        let configuredOutput = ProcessInfo.processInfo.environment["SIDEY_STORE_SNAPSHOT_DIR"]
            .map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? (FileManager.default.fileExists(atPath: snapshotSentinel)
                ? URL(fileURLWithPath: snapshotSentinel, isDirectory: true)
                : nil)
        if let configuredOutput {
            try FileManager.default.createDirectory(
                at: configuredOutput,
                withIntermediateDirectories: true
            )
        }

        for (scenario, configureState) in [
            (
                "owned-error",
                { (model: AppModel) in
                    model.apply(commerceState: CommerceState(
                        product: products[0],
                        googleConnected: true,
                        entitlementStatus: "active",
                        latestOrderStatus: "approved"
                    ))
                    model.setCommercePurchaseState(
                        .error("두 번째 상품만 상태를 불러오지 못했습니다. 다른 상품은 그대로 사용할 수 있습니다."),
                        productID: products[1].id
                    )
                }
            ),
            (
                "loading-confirming",
                { (model: AppModel) in
                    model.apply(commerceState: CommerceState(
                        product: products[0],
                        googleConnected: true,
                        entitlementStatus: nil,
                        latestOrderStatus: nil
                    ))
                    model.setCommerceWorking(true, productID: products[0].id)
                    model.setCommercePurchaseState(.confirming, productID: products[1].id)
                }
            )
        ] {
            for (sizeName, size) in [
                ("minimum", CGSize(width: 860, height: 640)),
                ("default", SettingsWindowController.settingsContentSize)
            ] {
                for scheme in [ColorScheme.light, .dark] {
                    let data = try renderSettings(
                        size: size,
                        colorScheme: scheme,
                        commerceProducts: products
                    ) { model in
                        model.activeSettingsPage = .store
                        configureState(model)
                    }
                    XCTAssertGreaterThan(data.count, 10_000)
                    if let configuredOutput {
                        let mode = scheme == .light ? "light" : "dark"
                        try data.write(
                            to: configuredOutput.appending(
                                path: "store-\(scenario)-\(sizeName)-\(mode).png"
                            )
                        )
                    }
                }
            }
        }
    }

    func testProductionStoreRendersAllLockedCardsWithoutRefreshingCommerce() throws {
        var refreshCalls = 0
        var purchaseCalls = 0
        var actions = SettingsActions.empty
        actions.onRefreshCommerceState = { _ in refreshCalls += 1 }
        actions.onPurchase = { _ in purchaseCalls += 1 }
        let snapshotSentinel = "/private/tmp/sidey-store-snapshots"
        let outputDirectory = ProcessInfo.processInfo.environment["SIDEY_STORE_SNAPSHOT_DIR"]
            .map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? (FileManager.default.fileExists(atPath: snapshotSentinel)
                ? URL(fileURLWithPath: snapshotSentinel, isDirectory: true)
                : nil)
        if let outputDirectory {
            try FileManager.default.createDirectory(
                at: outputDirectory,
                withIntermediateDirectories: true
            )
        }

        let sizes: [(String, CGSize)] = [
            ("minimum", CGSize(width: 860, height: 640)),
            ("default", CGSize(width: 1000, height: 760)),
        ]
        let schemes: [ColorScheme] = [.light, .dark]
        for (sizeName, size) in sizes {
            for scheme in schemes {
                let data = try renderSettings(
                    size: size,
                    colorScheme: scheme,
                    storeAvailability: .comingSoon,
                    actions: actions
                ) { model in
                    model.activeSettingsPage = .store
                }
                XCTAssertGreaterThan(data.count, 10_000)
                if let outputDirectory {
                    let mode = scheme == .light ? "light" : "dark"
                    try data.write(
                        to: outputDirectory.appending(
                            path: "store-locked-\(sizeName)-\(mode).png"
                        )
                    )
                }
            }
        }

        XCTAssertEqual(CommerceCatalog.products.count, 33)
        XCTAssertEqual(refreshCalls, 0)
        XCTAssertEqual(purchaseCalls, 0)
    }

    func testProfileCosmeticTileOpensOwnedDetailAndSelectsDefault() {
        var requests: [(CommerceProductKind, String?)] = []
        var detailedProductIDs: [String] = []
        let productTile = ProfileCosmeticTile(
            kind: .bubble,
            product: .bunnyPinkBubble,
            selectedCharacterID: PixelCharacterCatalog.pixelHamsterID,
            isSelected: false,
            isPending: false,
            isDisabled: false,
            onSelect: { requests.append(($0, $1)) },
            onShowDetail: { detailedProductIDs.append($0) }
        )
        productTile.requestPrimaryAction()

        let defaultTile = ProfileCosmeticTile(
            kind: .bubble,
            product: nil,
            selectedCharacterID: PixelCharacterCatalog.pixelHamsterID,
            isSelected: false,
            isPending: false,
            isDisabled: false,
            onSelect: { requests.append(($0, $1)) },
            onShowDetail: { detailedProductIDs.append($0) }
        )
        defaultTile.requestPrimaryAction()

        let blockedTile = ProfileCosmeticTile(
            kind: .bubble,
            product: .butterChickBubble,
            selectedCharacterID: PixelCharacterCatalog.pixelHamsterID,
            isSelected: false,
            isPending: false,
            isDisabled: true,
            onSelect: { requests.append(($0, $1)) }
        )
        blockedTile.requestSelection()

        XCTAssertEqual(detailedProductIDs, [CommerceProduct.bunnyPinkBubble.id])
        XCTAssertEqual(requests.count, 1)
        XCTAssertEqual(requests[0].0, .bubble)
        XCTAssertNil(requests[0].1)
    }

    func testOwnedProfileCosmeticsRenderAtSupportedSizesAndAppearances() throws {
        let userID = UUID()
        let roomID = UUID()
        let snapshotSentinel = "/private/tmp/sidey-profile-cosmetic-snapshots"
        let outputDirectory = ProcessInfo.processInfo.environment["SIDEY_PROFILE_SNAPSHOT_DIR"]
            .map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? (FileManager.default.fileExists(atPath: snapshotSentinel)
                ? URL(fileURLWithPath: snapshotSentinel, isDirectory: true)
                : nil)
        if let outputDirectory {
            try FileManager.default.createDirectory(
                at: outputDirectory,
                withIntermediateDirectories: true
            )
        }
        for (sizeName, size) in [
            ("minimum", CGSize(width: 860, height: 640)),
            ("default", CGSize(width: 1000, height: 760)),
        ] {
            for scheme in [ColorScheme.light, .dark] {
                for scrollToBottom in [false, true] {
                    let data = try renderSettings(
                        size: size,
                        colorScheme: scheme,
                        scrollToBottom: scrollToBottom,
                        storeAvailability: .comingSoon
                    ) { model in
                        model.activeSettingsPage = .profile
                        model.apply(
                            snapshot: BackendSnapshot(
                                profile: Profile(
                                    id: userID,
                                    nickname: "꾸미기친구",
                                    characterID: PixelCharacterCatalog.pixelHamsterID,
                                    equippedBubbleStyleID: CommerceProduct.bunnyPinkBubble.catalogItemID,
                                    equippedThrowableID: CommerceProduct.toyCannon.catalogItemID
                                ),
                                rooms: [Self.room(id: roomID, name: "꾸미기방")],
                                activeEntitlementKeys: Set(CommerceCatalog.cosmeticProducts.map(\.entitlementKey))
                            ),
                            currentUserID: userID
                        )
                    }
                    XCTAssertGreaterThan(data.count, 10_000)
                    if let outputDirectory {
                        let mode = scheme == .light ? "light" : "dark"
                        let placement = scrollToBottom ? "bottom" : "top"
                        try data.write(to: outputDirectory.appending(
                            path: "profile-cosmetics-\(sizeName)-\(mode)-\(placement).png"
                        ))
                    }
                }
            }
        }
    }

    private func renderSettings(
        size: CGSize,
        colorScheme: ColorScheme,
        scrollToBottom: Bool = false,
        commerceProducts: [CommerceProduct] = CommerceCatalog.products,
        storeAvailability: StoreAvailability = AppReleaseChannel.resolve().storeAvailability,
        actions: SettingsActions = .empty,
        configure: (AppModel) -> Void = { _ in }
    ) throws -> Data {
        var preferences = AppPreferences.defaults
        preferences.onboardingComplete = true
        preferences.overlayRegion.screenIdentifier = "display:main"
        let model = AppModel(preferences: preferences, commerceProducts: commerceProducts)
        model.activeSettingsPage = .app
        model.availableScreens = [
            OverlayScreenOption(id: "display:main", name: "내장 디스플레이"),
            OverlayScreenOption(id: "display:secondary", name: "보조 디스플레이")
        ]
        configure(model)

        let root = SettingsRootView(
            model: model,
            actions: actions,
            storeAvailability: storeAvailability
        )
            .environment(\.colorScheme, colorScheme)
            .frame(width: size.width, height: size.height)
        let hostingView = NSHostingView(rootView: root)
        hostingView.frame = CGRect(origin: .zero, size: size)
        let window = NSWindow(
            contentRect: hostingView.frame,
            styleMask: [.borderless],
            backing: .buffered,
            defer: false
        )
        window.contentView = hostingView
        hostingView.layoutSubtreeIfNeeded()
        hostingView.displayIfNeeded()
        if scrollToBottom,
           let scrollView = hostingView.descendants(of: NSScrollView.self)
               .filter({ ($0.documentView?.bounds.height ?? 0) > $0.contentView.bounds.height })
               .max(by: {
                   ($0.documentView?.bounds.height ?? 0) < ($1.documentView?.bounds.height ?? 0)
               }),
           let documentView = scrollView.documentView {
            scrollView.contentView.scroll(
                to: NSPoint(
                    x: 0,
                    y: max(0, documentView.bounds.height - scrollView.contentView.bounds.height)
                )
            )
            scrollView.reflectScrolledClipView(scrollView.contentView)
            hostingView.layoutSubtreeIfNeeded()
            hostingView.displayIfNeeded()
        }

        let bitmap = try XCTUnwrap(hostingView.bitmapImageRepForCachingDisplay(in: hostingView.bounds))
        hostingView.cacheDisplay(in: hostingView.bounds, to: bitmap)
        return try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
    }

    private static func room(id: UUID, name: String, members: [RoomMember] = []) -> Room {
        Room(
            id: id,
            name: name,
            ownerID: UUID(),
            members: members,
            inviteCodeHint: "••••-••AA",
            inviteVersion: 1
        )
    }
}

private extension NSView {
    func descendants<T: NSView>(of type: T.Type) -> [T] {
        subviews.flatMap { view in
            let match = (view as? T).map { [$0] } ?? []
            return match + view.descendants(of: type)
        }
    }
}
