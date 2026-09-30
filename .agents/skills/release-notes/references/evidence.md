# Release-note Evidence

Use this reference while researching the exact release range.

## Establish immutable endpoints

For Windows, resolve the previous published regular `windows-v<version>` tag and the target
commit/tag. For an App Store note, use App Store Connect evidence to identify the previous
published build and exact submitted build, then resolve the private source commit that produced
each build. Deleted macOS Direct `v<version>` tags are not App Store baselines and must not be
recreated. Confirm that the source baseline is an ancestor of the target and record both full
commit IDs. Do not treat a staged manifest, draft release or branch name as proof of a shipped
endpoint.

Run the skill-local collector from the repository root:

```text
python3 scripts/skills/release-notes/collect_release_evidence.py --base <previous-tag> --target <target-commit> --target-tag <release-tag> --output <temporary-json-path>
```

For App Store evidence, omit `--target-tag` and pass the two verified source commits:

```text
python3 scripts/skills/release-notes/collect_release_evidence.py --base <previous-published-source-commit> --target <submitted-source-commit> --output <temporary-json-path>
```

The collector is read-only. It follows the target's first-parent history so a traditional merge is represented once and SIDEY's squash integrations remain one record each. It resolves pull requests through GitHub's commit-to-pulls API because SIDEY squash subjects intentionally contain the Korean PR title without a `(#number)` suffix.

Source commit and pull-request evidence comes from the private `sidey-app/SIDEY-source` repository. The public `sidey-app/SIDEY` repository owns Windows release artifacts and URLs but not current source history. Do not substitute public artifact tags for the source comparison range. App Store evidence has no public GitHub release URL.

If GitHub authentication or commit metadata is unavailable, report `BLOCKED`. Do not fall back to guessing PR numbers from commit messages. More than one exact PR association for one integration is `ACTION REQUIRED`.

## Inspect and classify

For every collected integration:

1. Inspect the changed paths and relevant hunks, not only the title or PR body.
2. Confirm whether the selected platform artifact consumes the change.
3. Identify a concrete user-visible outcome or exclude the integration with a reason.
4. Verify claims with tests, manifests, artifact evidence or current behavior as appropriate.

Normally exclude release-note-only, version bookkeeping, CI-only and internal refactoring changes unless they produce a user-visible result for the artifact. A pull request may justify multiple bullets when it contains distinct user outcomes. Several pull requests may share one bullet when they implement one coherent outcome.

## Private attribution ledger

Use the pull-request opener returned by GitHub, not the merging account or Codex coauthor. For a direct commit without a pull request, record its full commit ID and GitHub commit-author login. Missing PR association or author resolution is ACTION REQUIRED; do not invent values.

Keep every integration in the private included/excluded ledger with source evidence, author, outcome or exclusion reason, and the corresponding public category/bullet or localized archive entry. Public notes contain no PR numbers, commit IDs or authors. The format validator verifies exact collector endpoint references when evidence is supplied; it cannot map unattributed public prose to source records. Review that mapping manually and report the attribution review separately from format validation.

For faithful copying or translation of macOS website notes, record the official App Store product/lookup version, publication date and exact published text. This is sufficient provenance for translation of those outcomes, without inferring source or build identities. For original notes researched from diffs, verify the published App Store version and build with App Store Connect evidence and resolve the exact submitted and prior published source endpoints. A listing's marketing version alone does not establish the build or source. Do not substitute historical Direct GitHub releases. Both website archives retain version 2.0 and later; earlier Windows releases remain on GitHub with archive links that have no version anchor.
