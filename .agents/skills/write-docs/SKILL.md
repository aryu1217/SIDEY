---
name: write-docs
description: Create or revise evidence-based SIDEY macOS or Windows developer documentation. Use for native implementation, contributor, debugging, deployment, localization, logging or code-style guides; do not use for public README or release copy, website or policy copy, repository-wide product documents, backend documentation, review-only requests or implementation-only work.
---

# Write Developer Documentation

Write native developer guides whose claims, commands and links a contributor can verify against the current repository.

Apply the repository-root `AGENTS.md` and every nested `AGENTS.md` governing the target document and its evidence. Then read only the relevant platform reference:

- For macOS, read [references/macos.md](references/macos.md).
- For Windows, read [references/windows.md](references/windows.md).
- For Korean prose, also read [references/korean-writing-style.md](references/korean-writing-style.md); do not load it for an English-only change.

## Establish the documentation contract

Follow the [logical task and Git boundaries](../remote-branch-workflow/SKILL.md#logical-task-and-git-boundaries).
Documentation needed to accurately explain the current feature or fix belongs
to that logical task. Reuse its task-owned worktree and branch, subject to
path ownership, and include the documentation in the same PR even if it uses a
focused `docs` commit. Invoking this skill does not start a documentation-only
task.

1. Read the target guide and any edition or neighboring guide required by its path rules.
2. Identify the contributor problem and the decision or procedure the guide must support.
3. Trace every behavioral claim to current native code, tests, project metadata, scripts, packaging inputs or CI. Source code and machine-readable contracts own exact current values; current product and architecture documents define repository-wide behavior, while decision records explain long-lived rationale.
4. Mark planned behavior and manual procedures as such. Do not present either as implemented or verified.
5. A documentation-only task is an independent user request to change only documentation. Keep implementation changes and product decisions outside that request unless the user authorizes expanding it; report any gap needed for accurate documentation. Documentation work inside an authorized feature task may accompany its implementation on the same branch. Split only when the logical-task contract or repository ownership rules require it.

## Draft from evidence

- Explain the concrete failure, maintenance cost or contributor choice before introducing a rule.
- Keep exact identifiers, paths, commands, limits and platform terms when they help the reader find the evidence.
- Link to maintained details instead of copying contracts across guides.
- Preserve useful text that still matches the implementation. Add a new guide only when the subject cannot fit an existing guide without mixing unrelated reasons to change.

## Verify claims, commands and links

- Re-read each cited implementation source and ensure the prose does not promise more than it proves.
- Read referenced scripts and their direct callers before documenting a command. Include required parameters, the expected working directory and nearby warnings for installation, publication, elevation, GUI, network, process or machine-state effects.
- Run the narrowest safe repository check that proves a behavioral claim. Documentation-only changes do not require an application build when repository evidence is sufficient.
- Resolve every relative Markdown link from the containing file and search renamed paths for stale references.
- Compare required paired editions for equivalent technical claims, commands, headings and language-appropriate links; do not invent pairing where the platform rules do not require it.
- Verify code examples as executable code or label them clearly as pseudocode.
- Search changed text for stale product names, paths, counts, versions, platform promises and unsupported availability claims.
- Run `git diff --check` and inspect every changed path against the branch base.

Report documents changed, implementation evidence inspected, checks run and unresolved implementation or product-decision gaps.
