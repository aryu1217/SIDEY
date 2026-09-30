# SIDEY Release-note Format

Read this reference before writing a canonical Windows GitHub body, website archive entry or App Store note.

## Public change categories

Lead with a short verified user outcome. Classify changes in this order, omitting empty categories:

- `## 새로운 기능`: newly available features.
- `## 개선 사항`: changes to existing behavior.
- `## 버그 수정`: corrected defects.

Describe concrete user outcomes in single-line bullets. Split mixed changes and fixes when needed. Public change copy never displays PR numbers, commit IDs or author names. A legally required full commit identifier for corresponding source may remain only in the exact `## 소스 코드 및 라이선스` section; PR numbers and authors remain prohibited there. Preserve those references, authors and outcome mappings only in the private evidence ledger. Do not expose private source links or public repository comparison URLs.

```markdown
새 기능을 더하고 프로그램 안정성을 개선했어요.

## 새로운 기능

- 최근 기록에서 메시지를 바로 보낼 수 있어요.

## 개선 사항

- 설정의 안내 문구를 더 쉽게 읽을 수 있어요.

## 버그 수정

- 창 크기를 바꿀 때 앱이 종료되던 문제를 고쳤어요.

## 다른 언어로 보기

- [한국어](https://sidey-app.github.io/SIDEY/ko/whats-new/windows/#v2.0.0)
- [English](https://sidey-app.github.io/SIDEY/en/whats-new/windows/#v2.0.0)
- [日本語](https://sidey-app.github.io/SIDEY/ja/whats-new/windows/#v2.0.0)
- [繁體中文](https://sidey-app.github.io/SIDEY/zh-hant/whats-new/windows/#v2.0.0)
```

## Windows GitHub navigation and website retention

Every Windows GitHub body ends with the exact `## 다른 언어로 보기` section and the four bullet links shown above, in that order. Use the exact target version in each anchor. Website archives retain only version 2.0 and later for both platforms. For earlier Windows releases, keep the GitHub note but link to `/{locale}/whats-new/windows/` without an anchor. Do not link an omitted historical version anchor.

Use the same verified outcomes on the website in every Windows consumer locale defined in `assets/v1/ui-localizations.json`. Store Windows release metadata in `website/src/data/release-notes/recent.json`, macOS metadata in `website/src/data/release-notes/macos.json`, and localized copy in `website/src/i18n/locales/<locale>.json`; archive rendering filters versions to 2.0 and later. Preserve historical installation guidance, purchase limitations and other user actions for the version they describe. Non-change guidance belongs in notes, not a change category. Both landing-page release-information links lead to the localized website archive. Show the verified published macOS version, never a staged target version.

## macOS website and App Store notes

Maintain a localized macOS website archive for verified published App Store releases, rather than redirecting the archive to the store. Retain version 2.0 and later. Faithful copying, categorization or translation of an already published macOS App Store note may use the official App Store product page or lookup response as evidence of its version, publication date and exact release-note text. Record that public provenance and do not infer additional functionality. This does not require unpublished build identities or a prior source range. Original note research from source diffs and App Store submissions still require exact submitted/published and prior published builds, source endpoints and App Store Connect evidence. If no verified retained releases exist, show an accurate localized empty state.

App Store Connect text remains a separate artifact with its verified destination length and localization requirements. Keep attribution in the private ledger. Never write it over `docs/releases/v<version>.md`: those files are historical copies of deleted Direct GitHub releases, which must not be revised, recreated or republished.

## User-requested preamble or sections

Preserve requested preamble wording before a thematic break, then provide the summary and applicable categories. Add installation steps, warnings, limitations or another section only when requested or when verified user action or risk requires them. Do not add a title or date by default; the canonical Windows file is also the GitHub Release body.
