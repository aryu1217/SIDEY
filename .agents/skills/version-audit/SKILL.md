---
name: version-audit
description: Assess whether a target SIDEY macOS or Windows artifact needs a version bump from its actual release diff, recommend or apply the minimum valid version/build, and report consistency evidence. Use for version planning, candidate or stable release preparation, and release-version audits; do not use for ordinary source edits, release-note writing, or release publication.
---

# Version Audit

Determine the version implied by the shipped artifact, then let repository tooling prove that
the chosen metadata agrees. Both platforms share head/yearweek and keep independent build lines.

## Inputs

Establish the target platform and commit, the intended artifact or distribution, and whether
the request is advisory or asks to apply metadata changes. Record any user-selected version
class, but do not accept one below the evidence-based minimum.

Start with `release/version.json` and the generated `release/macos.json` or
`release/windows.json` mirror. For macOS, obtain App Store
Connect publication/build evidence and the exact source provenance of the last shipped
App Store artifact; the manifest describes the target build, not publication status. Do not
use an archived Developer ID tag as proof of an App Store release. The maintained macOS
Xcode scheme and app product are both named `SIDEY`; legacy Xcode identifiers are not
current target evidence. For Windows, confirm the matching
`windows-v<releaseVersion>` tag is a
published regular release. If a target is intentionally staged ahead of publication, identify
the previous shipped platform artifact as the comparison baseline. If baseline, target,
artifact impact or provenance cannot be established, return `BLOCKED` with the missing evidence.

## Audit

1. Inspect commits, changed paths and relevant hunks from the platform's verified shipped
   commit through the target commit. Commit titles alone are insufficient.
2. Decide whether the distributed platform artifact or a packaging input it consumes changes.
   A shared change counts only for a platform that consumes it. Return `NONE` for web-, server-,
   documentation-, or contributor-only changes that do not alter an app artifact.
3. Read the current HeadVer `head` and ISO `yearweek` from `release/version.json`. Keep the
   selected head unless the release owner explicitly changes it. Validate the ISO week-year
   at the intended build date and keep it identical across macOS and Windows.
4. Propose each platform's exact `{head}.{yearweek}.{build}`. Reject malformed, reused or
   decreasing values. Windows official build numbers come from the remote reservation ledger;
   failed builds may leave gaps.
5. macOS supports only the App Store target. Before proposing a distributable build, determine
   the greatest build already consumed by App Store Connect and add one. Repository metadata
   cannot prove external upload history: label the build `PROVISIONAL` and block upload
   readiness when that history is unavailable. Do not restart or lower existing version/build
   values when retiring a distribution channel.
6. Read `scripts/skills/verify_release_consistency.py` for the current mirrors and run the mode
   matching the audited platform. macOS validates the App Store target build contract, not
   review or publication status. Windows publication uses the strict platform check.
   Candidate allowances validate only their named staged states.

When the user asks to apply the decision, change only the metadata in scope and rerun the
affected deterministic checks. An audit does not by itself authorize edits.

## Evidence

Return this structure, adapting fields only when a platform does not have them:

```text
Platform / target commit / artifact
Baseline: manifest version, build, tag, tag commit
Comparison: exact range, artifact affected YES/NO, consumed shared changes
Impact: changed artifact and platform evidence
Decision: shared head/yearweek, platform build, proposed HeadVer
Build history: VERIFIED / PROVISIONAL / BLOCKED
Consistency: source, manifest, Windows updater or App Store target, release note, public metadata, tag uniqueness
Verdict: READY / ACTION REQUIRED / BLOCKED
Missing or conflicting evidence
```

Do not create or push tags, build or upload release artifacts, publish a GitHub Release, submit
to a store, or deploy. Those operations require separate explicit authorization and remain
owned by the repository's release automation.
