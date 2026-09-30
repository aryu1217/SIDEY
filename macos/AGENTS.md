# SIDEY macOS Instructions

These instructions apply to `macos/**`. Read the repository-root `AGENTS.md` first. The root instructions also route macOS-owned files outside this directory, including `scripts/macos/**`, macOS release scripts and macOS-specific GitHub workflows, back to this file.

## Ownership and scope

- Make macOS implementation changes on a task-owned `macos/*` branch and worktree. Keep Windows implementation files out of the change, and prepare shared product, protocol, asset-source, website or repository-policy changes separately on `shared/*`.
- Keep the client native in SwiftUI, AppKit and SpriteKit. The Mac App Store is the only maintained macOS distribution. Preserve its existing signing, entitlement, Apple authentication, Keychain and StoreKit identities. Do not restore Direct targets, Sparkle updates or DMG packaging. Keep the independent recording tool.
- Treat `macos/SIDEY.xcodeproj`, its shared schemes, resolved package file, property lists and entitlements as executable contracts. Do not restate dependency or deployment versions in prose unless the version itself is being changed.
- Preserve the product privacy boundary, explicit interaction mode, click-through overlay
  default, Keychain behavior and backend ownership defined by the root instructions,
  `docs/product/` and `docs/architecture.md`.

## Build and validation

GitHub Actions is disabled, so validate macOS changes locally on a Mac from the exact task worktree. Start with the narrowest affected XCTest, Python asset/provenance test or structural check. When a change can affect the shipped macOS application, run the maintained native route from the repository root:

```sh
./scripts/macos/tests/test_native.sh
```

This route verifies content assets, runs the macOS Python tests, and runs `SIDEY` XCTest coverage excluding `WindowPolicyTests`. It does not launch or test the independent recording app. Window/focus tests and recording tests are separate, explicitly selected checks: use `SIDEY_RUN_WINDOW_POLICY_TESTS=1 ./scripts/macos/tests/test_native.sh -only-testing:SIDEYTests/WindowPolicyTests` for the former and `./scripts/macos/tests/test_recording.sh` for the latter. Do not add them back to ordinary integration checks or run them for unrelated changes. Read narrower scripts before invoking them and use the maintained wrapper for final integration evidence.

Signing, notarization, packaging, app launch, Keychain prompts, StoreKit operations, release access, network-backed integration and other machine- or account-state changes require matching user authorization. A successful build does not prove runtime or distribution behavior that was not observed.

## Documentation and specialist skills

Keep general macOS contributor guides under `macos/docs/**`. Preserve purpose-specific material in `macos/Review/**` and the recording tool's own `macos/Recording/README.md`; do not force the Windows paired-language layout onto macOS documents.

Use the narrowest applicable repository-local skill:

- [`code-review`](../.agents/skills/code-review/SKILL.md) for a substantive macOS application or distribution review;
- [`write-tests`](../.agents/skills/write-tests/SKILL.md) for choosing and implementing a macOS test boundary;
- [`write-docs`](../.agents/skills/write-docs/SKILL.md) for evidence-based macOS developer documentation.

These skills add task-specific judgment and do not replace the always-on repository and path rules.
