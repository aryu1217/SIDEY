---
name: release-notes
description: Research, draft and validate a user-facing SIDEY release note from its exact shipped commit and pull-request range, with private attribution and inclusion or exclusion evidence. Use for Windows GitHub Release bodies, localized Windows or macOS website archives, or an explicitly identified App Store Connect note; do not use for deleted direct-distribution macOS releases, README, developer documentation, version selection or release publication.
---

# Release Notes

Produce the canonical release body for one platform artifact. General documentation rules remain in `docs/AGENTS.md`; this skill owns release-specific investigation, private attribution, localized copy and format validation.

## Inputs

Establish the platform, target version and commit, intended artifact, distribution channel, and previous published regular release for that channel. Use the matching `release/*.json` as release metadata. A staged target manifest is not evidence that its tag or App Store build has shipped. Stop with `BLOCKED` if the target, artifact, channel or comparison baseline is ambiguous.

When `release/macos.json` uses the `appstore` channel, existing `docs/releases/v<version>.md` files are private historical copies of deleted Direct GitHub Release bodies. Never revise, republish, recreate or reinterpret one as an App Store note merely because its marketing version matches. Original App Store note research requires the exact submitted/published build, the prior published App Store build, App Store Connect evidence and an explicit destination/localization request. If any of those are missing, report `BLOCKED`; the manifest alone does not authorize or identify a canonical note file. Faithful copying, categorization or translation of an already published macOS App Store note may use the official App Store product page or lookup response as evidence of its version, publication date and exact release-note text. Record that public provenance and do not infer additional functionality. This does not require unpublished build identities or a prior source range. Original note research from source diffs and App Store submissions still require exact submitted/published and prior published builds, source endpoints and App Store Connect evidence.

## Collect and classify evidence

For faithful translation of an already public macOS note, save the official product/lookup version, publication date and exact source text as provenance and verify each translated point against it. Do not require the source-range collector for that task. The following collector procedure applies to original notes researched from source diffs.

Read [references/evidence.md](references/evidence.md), then use the read-only
[collector](../../../scripts/skills/release-notes/collect_release_evidence.py)
to inventory first-parent integrations, associated pull requests, direct
commits, authors and changed paths from the private source repository in the
exact baseline-to-target range. The
inventory is a routing aid, not sufficient evidence for release copy: inspect
the relevant hunks, pull-request context and tests or artifact evidence.

Include only changes consumed by the target platform artifact. Keep macOS and Windows independent. Exclude other-platform work, future work, release-note preparation itself, and internal-only changes without a meaningful user outcome. Record every integration as included or excluded with a reason; unresolved PR association or author attribution is `ACTION REQUIRED`, not a value to invent.

## Draft and verify

Read [references/format.md](references/format.md) before drafting. Translate implementation evidence into a short final-user summary followed by one-sentence change bullets. Preserve installation actions, compatibility limits, data-risk warnings, signing status and known limitations only when users need them or the user requests a separate section. Do not claim unverified functionality, security, signing, testing, availability or release completion.

Write or revise `docs/releases/windows-v<version>.md` for Windows. Do not edit the historical `docs/releases/v<version>.md` macOS Direct copies or recreate their deleted public releases. For an explicitly evidenced App Store submission, prepare only the requested App Store Connect destination and localization, keep PR/commit attribution in the evidence ledger rather than the customer-facing note, and follow the destination's length limit. Windows GitHub bodies use a short summary and nonempty `## 새로운 기능`, `## 개선 사항`, `## 버그 수정` categories in that order, followed by `## 다른 언어로 보기` and four official localized website links. Never display PR numbers, commit IDs or authors in public change copy; preserve a legally required full corresponding-source commit identifier only under `## 소스 코드 및 라이선스`; retain all attribution and source-to-outcome mapping in the private evidence ledger. Do not add a release title, date, repository comparison link, installation or limitations section by habit. The public distribution repository does not contain source history, so a public `compare` URL is not an application changelog. Do not expose or link the private source repository from a public release body.

Save the collector output outside tracked source. For a Windows GitHub Release, run the
[format validator](../../../scripts/skills/release-notes/validate_release_note.py)
with that evidence and the exact baseline and target tags. For an App Store note, verify the
requested localization and destination length against App Store Connect instead; do not run the
GitHub Release-body validator or invent GitHub tags. Then run the relevant
`scripts/skills/verify_release_consistency.py` mode and validate changed links. Recheck every
included outcome against the collected source evidence; for Windows also recheck each public
bullet's source-to-outcome mapping and author in the private evidence ledger; format validation does not perform that manual review.

For website archives, preserve the same verified outcomes in every Windows consumer locale from `assets/v1/ui-localizations.json`, with wording appropriate to each language and region. Both platform archives retain only version 2.0 and later. Retained Windows GitHub notes link directly to localized version anchors; earlier releases remain on GitHub and link to localized Windows archives without an anchor. Maintain macOS archive entries from verified published App Store notes or exact-build source evidence rather than redirecting to the store. Write Windows archive metadata to `website/src/data/release-notes/recent.json` and macOS metadata to `website/src/data/release-notes/macos.json`, with complete localized copy in `website/src/i18n/locales/<locale>.json`; archive rendering applies the 2.0+ retention filter. Show a localized empty state when no retained verified releases exist, without mentioning the retention threshold; do not invent an entry from a staged manifest or historical Direct note. The landing-page release-information links for both platforms lead to these localized website archives, and the displayed macOS version must come from a verified published release rather than the staged build manifest.

## Evidence

```text
Platform / version / target commit / artifact
Baseline tag and commit / exact comparison range
Included: PR or commit, author, source evidence -> user-facing outcome
Excluded: PR or commit, author, source evidence -> exclusion reason
Canonical release-note path / localized archive destinations
Private source-to-outcome attribution review: reviewed / unresolved
Validation: note format, attribution, exact source range, manifest, links, deterministic checks
Verdict: READY / ACTION REQUIRED / BLOCKED
Unverified claims or missing evidence
```

Do not change milestones or pull requests, create or push a tag, upload an artifact, create or edit a public GitHub Release, submit to a store, or deploy. Publication remains a separate explicitly authorized release operation.
