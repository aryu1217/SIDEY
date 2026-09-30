---
name: write-tests
description: Design, create, revise or remove automated tests for SIDEY macOS or Windows code and distribution contracts. Use when changing native tests, repairing test reliability or explicitly deciding a native test boundary; do not use merely to run existing checks, review a diff, or change production code or prose without test work.
---

# Write Tests

Write the smallest reliable test that proves the requested native behavior at the narrowest useful boundary.

Follow the [logical task and Git boundaries](../remote-branch-workflow/SKILL.md#logical-task-and-git-boundaries).
Tests needed to complete the current feature or fix belong to that task's
existing worktree, branch, and PR, subject to path ownership. A focused `test`
commit or invoking this skill does not create a separate test task or PR.

Apply the repository-root `AGENTS.md`, every nested `AGENTS.md` governing the changed paths, and the authoritative behavior or architecture documents relevant to the change. Read the affected production code, its callers and the nearest tests. State the trigger, observable result and consequential failure before choosing a layer.

Read only the selected platform reference:

- For macOS, read [references/macos.md](references/macos.md).
- For Windows, read [references/windows.md](references/windows.md).

## Keep the test reliable

- Name the concrete trigger and observable result.
- Assert externally meaningful payloads, state transitions, ordering, cleanup, recovery or artifact structure instead of mirroring implementation steps.
- Prefer fixed inputs, fakes, local protocol servers, completion signals and bounded timeouts.
- Use events, callbacks, cancellation or completion signals for asynchronous readiness. Use bounded polling or a policy-derived delay only when timing is itself part of the contract, and keep the margin explicit.
- Cover the consequential failure boundary as well as success. Add cases only when each protects a distinct rule.
- Keep tests independent of execution order, wall-clock date, machine locale, network access, user data and installed application state unless the selected authorized layer owns that dependency.

Read repository files directly when they are the contract, including project files, UI declarations, manifests, workflows, installer or package sources, localization catalogs and packaged assets. Do not test method bodies through source strings or depend on incidental whitespace, declaration order or private symbol names.

Do not invent a UI-automation stack. Prove behavior below the UI where possible and reserve maintained startup smoke or manual UI validation for behavior that cannot be established below that layer. Replace brittle tests only within the requested scope and preserve every real invariant at a behavioral, structural or artifact boundary.

## Validate

Run the nearest affected test or project first, then use the selected platform's maintained broader route when warranted. Startup, process, installer, signing, package, GUI and machine-level checks remain subject to the platform authorization boundary.

Report the behavior covered, tests replaced or removed, commands and results, and remaining manual, distribution or machine-level validation gaps.
