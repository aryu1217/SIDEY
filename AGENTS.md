# SIDEY Repository Instructions

## Project context

SIDEY is a macOS/Windows desktop overlay ambient messenger for private groups of up to twelve close friends. Each on-screen 2D pixel animal represents a real friend and shows presence, typing, and short text messages.

## Sources of truth

Read only the sources relevant to the task:

- Current product behavior: [`docs/product/`](docs/product/overview.md)
- Repository and system boundaries: [`docs/architecture.md`](docs/architecture.md)
- Long-lived decision rationale: [`docs/decisions/`](docs/decisions/README.md)
- Repeatable release operations: [`docs/operations/`](docs/operations/release.md)
- Backend ownership and catalog handoff: [`docs/BACKEND.md`](docs/BACKEND.md)
- Commerce metadata: [`assets/v1/commerce-catalog.json`](assets/v1/commerce-catalog.json)
- Asset metadata and platform support: [`assets/v1/manifest.json`](assets/v1/manifest.json)
- Shared product version and platform build counters: [`release/version.json`](release/version.json)
- Generated App Store target and public Windows release mirrors:
  [`release/macos.json`](release/macos.json) and [`release/windows.json`](release/windows.json)

Source code, project settings and machine-readable sources take precedence over current
documentation. Current product and architecture documents take precedence over decision
records, which explain historical rationale rather than current state. Use Git and pull
request history for ordinary implementation history. Do not duplicate machine-readable
values in documentation.

## Instruction routing

- Before changing `macos/**`, `scripts/macos/**`, or macOS-specific workflows, read [the macOS instructions](macos/AGENTS.md).
- Before changing `windows/**`, `scripts/windows/**`, or Windows-specific workflows, read [the Windows instructions](windows/AGENTS.md). Changes under `windows/docs/**` also follow [the Windows developer-document instructions](windows/docs/AGENTS.md).
- Before changing `website/**`, read [the public website instructions](website/AGENTS.md). Use [.agents/skills/web-verification/SKILL.md](.agents/skills/web-verification/SKILL.md) when a public-site change needs claim, build, or rendered-layout evidence.
- Before changing `docs/releases/**` or public release copy, read [the documentation and release instructions](docs/AGENTS.md). Use [.agents/skills/release-notes/SKILL.md](.agents/skills/release-notes/SKILL.md) only for a specific macOS or Windows release note or GitHub Release body.
- Nested `AGENTS.md` files add rules for their path. A root-started Codex session does not load a nested file automatically, so follow the routing above before touching that path.

## Backend ownership

Backend implementation lives in the private [sidey-app/sidey-backend](https://github.com/sidey-app/sidey-backend) repository. Supabase migrations, RLS, Edge Functions, App Store verification, database tests, operational metrics and backend deployment tools must be changed and verified in a current checkout of that repository under its own `AGENTS.md` and local checks. Do not recreate backend implementation here or deploy from historical SIDEY copies.

Keep native Supabase clients, the public website source and client-facing product contracts here. The canonical product and asset definitions remain `assets/v1/commerce-catalog.json` and `assets/v1/manifest.json`; backend catalog updates use a reviewed private source commit snapshot with recorded provenance. This repository generates only public web and native mirrors. See [docs/BACKEND.md](docs/BACKEND.md) for repository boundaries and catalog handoff. Repository migration does not itself deploy backend changes or remove previously public Git history.

## Work lifecycle

Use an isolated, task-owned worktree on a branch whose platform prefix matches the complete change. Treat `scripts/skills/workflow.py` as the executable source of truth for freshness, ownership, changed-path classification, checks, exact-head integration and primary-main refresh. Use [.agents/skills/app-verification/SKILL.md](.agents/skills/app-verification/SKILL.md) when an integrated app change needs a runtime verdict from exact-main provenance. Public releases, store uploads and production deployments are separate operations that require explicit authorization.

- Do not make implementation changes directly on `main`, switch or clean another task's dirty worktree, or overwrite unrelated user or agent changes.
- Keep the requested change focused. Do not include drive-by refactors, formatting, generated output, or documentation changes that are not required by the task.
- A failed, skipped, stale, or unavailable required build or test is not a passing result. Fix it or report the gap; do not integrate while a required check is failing.
- Review the full diff against its base before a commit or handoff. Validation must describe the exact commit and source snapshot tested. If source changes after validation, run the affected checks again.
- Integrate only the reviewed and checked head. Do not substitute a rebuilt, amended, or merely similar commit without repeating review and validation.

## Contribution, commit and pull request policy

[CONTRIBUTING.md](CONTRIBUTING.md) is the human-readable source of truth for
issues, branch boundaries, commit messages, validation and pull request
content. Apply it to both manual and agent-assisted work. Deterministic rules
remain enforced by the tracked validators and `scripts/skills/workflow.py`.

Use [.agents/skills/commit/SKILL.md](.agents/skills/commit/SKILL.md) when asked
to prepare a commit message or create a local commit. Use
[.agents/skills/create-pr/SKILL.md](.agents/skills/create-pr/SKILL.md) when
asked to draft or publish a pull request. Neither skill grants permission to
push, create a pull request, merge, release or deploy beyond the user's
explicit request. GitHub's repository settings own the generated squash
commit title and body; do not override them in the task workflow.

Codex-assisted new commits must include `Co-authored-by: codex <codex@openai.com>` while preserving the human Git author. The canonical mechanism is the repository hook installed once per clone with `python3 scripts/skills/commit/setup_codex_attribution.py`; it also covers linked worktrees. If an existing hook prevents installation, preserve it and connect the tracked hook through that clone's hook manager; if automatic attribution is unavailable, add the trailer once to the newly authored Codex commit. Do not attribute imported or replayed history, duplicate attribution enforcement in the commit-message validator, or rewrite existing history to add attribution.

## Automatic parallel agent work

- The coordinating agent must proactively create subagents when independent work can run alongside its own useful work and the expected time savings or verification benefit outweigh creation and coordination costs. This is an explicit instruction to delegate qualifying SIDEY work; do not wait for the user to request parallel agents or ask for confirmation each time.
- Use the minimum useful number of agents within the environment's available limits. Independent features, separate investigations, and verification that does not depend on unfinished changes are suitable candidates. Handle small edits, overlapping file changes, and dependent steps directly or sequentially.
- Follow the user's explicit request for solo or sequential work. If delegation is unavailable or capacity is exhausted, continue feasible work directly. All agents remain within the authorized task, platform boundaries, and the environment's permissions.
- The coordinating agent allocates the task worktree and disjoint file ownership before delegation. Subagents do not create more agents, expand their scope, or commit, push, create PRs, or integrate unless the coordinator explicitly assigns that repository-state operation.
- Stop source-mutating parallel work before final diff review and validation. The coordinating agent runs the final `check`/`finish` sequence serially and owns the integration and completion report.

## Branch and platform isolation

- Use `macos/<topic>` for macOS implementation, `windows/<topic>` for Windows implementation, and `shared/<topic>` for shared documentation, website, client protocol, or repository-wide work.
- Treat `main` as an integration and release branch. Do not implement features directly on `main`.
- Before editing, verify the current branch and working tree. Before committing or pushing, inspect every changed path relative to the branch base.
- A `macos/*` branch must not edit, move, delete, format, generate, build, test, or release Windows implementation files. This includes `windows/**` and Windows-specific workflows, installers, assets, and documentation.
- A `windows/*` branch must not edit, move, delete, format, generate, build, test, or release macOS implementation files. This includes `macos/**` and macOS-specific scripts, workflows, packages, assets, and documentation.
- Shared changes belong on `shared/*`. Do not mix new shared-file edits into a platform implementation commit. Land the shared change independently, then merge or cherry-pick that reviewed commit into the platform branch that needs it.
- Exception for an atomic Product Version transition: a `shared/*` branch may update `release/version.json` together with its generated macOS and Windows version mirrors (`release/macos.json`, `macos/Config/Version.xcconfig`, `release/windows.json`, and `windows/Version.props`) and the matching release version in `release/app-store-localizations.json`. The Product Version must increase, `windowsRevision` must reset to `0`, and `macBuild` must stay unchanged. The workflow verifies the source change against the branch base, checks the generated mirrors, and permits only the release version field to change in the App Store localization manifest; other platform paths remain isolated.
- Exception for a macOS build counter only: a `macos/*` branch may update `release/version.json` together with its generated `release/macos.json` and `macos/Config/Version.xcconfig` mirrors. Its `productVersion`, `windowsRevision`, schema and keys must stay unchanged, and `macBuild` must increase. The workflow compares the exact source against the branch base before accepting this exception.
- If a platform task reveals work needed on the other platform, record a follow-up instead of implementing it on the current branch.
- Do not switch or clean a dirty worktree owned by another task or agent. Create an isolated worktree on the correctly prefixed branch.
- macOS remains the reference implementation. Windows follows through its own branch without rewriting or opportunistically modifying macOS code.

## Non-negotiable rules

- The product name is `SIDEY`. Do not call it `같이온` or `같이ON` in product copy, code, package identifiers, or new documentation.
- Do not expand the MVP without an explicit product decision. In particular, do not add mobile/web clients, public discovery, groups over twelve, media/file transfer, calls, AI companions, or user-uploaded avatars.
- Keep the working macOS client native in SwiftUI/AppKit/SpriteKit and build the Windows client natively in C#/.NET/WinUI 3 with Win32 platform services. Do not reintroduce Godot.
- Build Windows on the shared `PixelCharacterCatalog` renderer with all five current characters from the first functional build. Validate the same renderer in an internal one-character hamster mode before promotion; do not create a hamster-only product implementation or expose that mode in Release builds. The existing macOS five-character client remains untouched while Windows validation runs.
- Treat Postgres as the source of truth for messages. Presence is for connection/activity state; Broadcast is for transient events such as typing.
- Enforce room membership, the twelve-member limit, and the five-room-per-user limit on the server. Client-only validation is insufficient. Nicknames and character choices may be duplicated.
- Apply Supabase RLS to user and room data. Never store invite codes in plaintext.
- Do not claim end-to-end encryption unless E2EE has actually been designed, implemented, and verified.
- Never collect screen contents, active application lists, keys typed in other applications, mouse coordinates, file contents, microphone audio, or camera video.
- The only global activity signals in scope are elapsed time since last system input and screen lock state. Typing status comes only from the SIDEY input field.
- Default overlay mode passes pointer input through to applications behind it. Interaction must be an explicit mode.
- Do not promise that the overlay appears above secure OS screens, DRM applications, elevated applications, or every exclusive-fullscreen game.

## Engineering priorities

Prefer correctness, long-running stability, low resource usage, privacy, and cross-platform behavior over visual polish or feature count. Keep pixel assets at 24×24 logical pixels with ten deterministic frames, integer nearest-neighbor scaling, no real-time shadows, and 30 FPS by default.

When behavior or scope changes, update the relevant current document under `docs/product/`
or `docs/architecture.md`. Add a file under `docs/decisions/` only for a long-lived decision
that meets that directory's inclusion rules. Keep ordinary change history in Git/PRs.
