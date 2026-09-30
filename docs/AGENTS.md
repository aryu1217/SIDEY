# SIDEY Documentation and Release Instructions

These instructions apply to `docs/**`.

## Document boundaries

- `product/**` describes current product behavior, `architecture.md` describes current
  system boundaries, `decisions/**` preserves only long-lived rationale, and `operations/**`
  contains repeatable procedures. Do not put plans, completed checklists or ordinary change
  history into current-state documents.
- Machine-readable source and code own exact prices, IDs, versions, build numbers, asset
  hashes and platform settings. Link to those sources instead of copying their values into
  Markdown. Use Git/PR history for ordinary implementation history.
- Public release writing is for users. Internal architecture, review evidence, operational history and contributor procedures may retain the technical detail needed for their audience; do not force public marketing style onto them.
- Windows contributor documentation under `windows/docs/**` follows `windows/docs/AGENTS.md`, not this directory's location and language layout.

## Release documentation contract

- `release/version.json` owns the shared HeadVer head/week and independent platform builds.
  `release/macos.json` and `release/windows.json` are generated release mirrors;
  native project settings and Windows update metadata are validated mirrors of the same source.
  The macOS mirror records an App Store target, not public review or publication status.
- Existing `docs/releases/v<version>.md` files are private historical copies of deleted direct-distribution GitHub Release bodies. Do not revise, republish or recreate those releases, and do not rewrite one as an App Store note merely because the marketing version matches. App Store release notes require the exact submitted build and App Store Connect evidence. `docs/releases/windows-v<releaseVersion>.md` remains the canonical Windows release body; historical revision-based releases keep their published identity and HeadVer releases use the generated Windows version.
- Release notes lead with a short verified user outcome and classify changes under `## 새로운 기능`, `## 개선 사항` and `## 버그 수정`, in that order, omitting empty categories. Public change copy must not display PR numbers, commit IDs or authors; a legally required corresponding-source identifier may remain only under `## 소스 코드 및 라이선스`; retain attribution and outcome mapping in the private evidence ledger. Do not link private source or public repository comparisons. Follow the release-notes skill for format and evidence.
- Windows GitHub bodies end with `## 다른 언어로 보기` and four official website links in Korean, English, Japanese and Traditional Chinese. Website release archives retain version 2.0 and later on both platforms. Link retained Windows versions directly to their version anchors; earlier GitHub notes link to each language's Windows archive without an unavailable anchor.
- The website maintains localized macOS release notes instead of redirecting the archive to the store. Faithful copying, categorization or translation of an already published macOS App Store note may use the official App Store product page or lookup response as evidence of its version, publication date and exact release-note text. Record that public provenance and do not infer additional functionality. This does not require unpublished build identities or a prior source range. Original note research from source diffs and App Store submissions still require exact submitted/published and prior published builds, source endpoints and App Store Connect evidence. Never recreate deleted macOS Direct GitHub releases or reinterpret their historical notes as App Store releases.
- Add installation steps, required warnings or limitations only when the user requests a separate section or verified user action or risk makes the information necessary. Keep implementation details only when users must act on them.
- Use the official website and GitHub Releases URLs. Do not claim signing, testing, compatibility, availability or release completion without evidence from the exact target commit and artifact.

Use [release notes](../.agents/skills/release-notes/SKILL.md) when researching or drafting the canonical note or GitHub Release body for a specific platform release. Use [version audit](../.agents/skills/version-audit/SKILL.md) when the release needs an evidence-based version or build decision. Validate versions and links with `scripts/skills/verify_release_consistency.py` and run `git diff --check`. Drafting documentation does not authorize tags, uploads, publication or deployment.

Website release data lives in `website/src/data/release-notes/recent.json` for Windows and `website/src/data/release-notes/macos.json` for macOS. Archive rendering filters out versions earlier than 2.0; translated entries use Korean, English, Japanese and Taiwan Traditional Chinese.
