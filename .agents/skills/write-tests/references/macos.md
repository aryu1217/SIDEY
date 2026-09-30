# macOS Test Reference

Read this reference only when changing macOS-owned tests. Apply `macos/AGENTS.md` first.

## Existing boundaries

- Use `macos/SIDEYTests/**` for application domain, state, infrastructure, AppKit, SpriteKit, commerce and distribution behavior exercised through XCTest.
- Use `macos/Recording/Tests/**` for the independent recording tool.
- Use `scripts/macos/tests/**` for deterministic Python checks of assets, metadata, packaging inputs and provenance contracts.
- Inspect Xcode project membership, schemes, property lists, entitlements, privacy manifests and packaged assets structurally when the declaration or file set is the contract.

Prefer a focused XCTest or Python test while iterating. `./scripts/macos/tests/test_native.sh` is the canonical broad route and runs asset verification, Python tests, the App Store XCTest scheme except `WindowPolicyTests`, and app version verification. The default integration route excludes window/focus UI tests and the independent recording tool tests; those tests remain available for explicit validation.

Run the window/focus suite separately when desktop focus can remain undisturbed:

```sh
SIDEY_RUN_WINDOW_POLICY_TESTS=1 ./scripts/macos/tests/test_native.sh -only-testing:SIDEYTests/WindowPolicyTests
```

Run the recording tool tests separately:

```sh
./scripts/macos/tests/test_recording.sh
```

Report whether either optional suite ran; a passing default route does not prove their behavior. Do not substitute application launch, signing, StoreKit, Keychain or network-backed checks without matching authorization.
