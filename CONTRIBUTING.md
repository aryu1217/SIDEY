# Reporting issues for SIDEY

SIDEY's public GitHub repository is used for official releases, the public
website, policy documents, and bug reports. Current application and website
source is maintained in a private repository.

## Report a bug

Search the existing [issues](https://github.com/sidey-app/SIDEY/issues) before
opening a new report. Include the steps needed to reproduce the problem, what
you expected, what happened, and the SIDEY and operating-system versions
involved.

Remove access tokens, invite codes, message contents, personal information,
and other secrets from logs and screenshots before posting them.

## External contributions

SIDEY does not currently accept external pull requests or submissions of code,
tests, documentation, translations, characters, audio, or other assets. Please
use GitHub Issues only for reproducible product bugs. Feature proposals and
general implementation submissions may be closed without review.

Previously accepted contributions keep their existing copyright, attribution,
agreements, and license terms. Closing external contribution does not revoke
the license granted for an earlier public revision; see
[LICENSING.md](LICENSING.md).

## Internal source work

The current application and website source is maintained in the private
`sidey-app/SIDEY-source` repository. The following process applies to
maintainers and authorized agents working there; it does not reopen external
code or asset contributions.

Keep each change focused on its issue or task. Read the relevant current
[product documents](docs/product/overview.md) and
[architecture](docs/architecture.md) when changing behavior. Use
[decision records](docs/decisions/README.md) for established boundaries;
current code and machine-readable sources own exact values.

### Worktree and branch

Do not implement on `main` or reuse another task's dirty worktree. Start from
a clean primary checkout and give the task an isolated worktree:

```shell
python3 scripts/skills/workflow.py doctor
python3 scripts/skills/workflow.py start <task> --platform <shared|macos|windows> --worktree <path>
```

Use `macos/<topic>` for macOS implementation, `windows/<topic>` for Windows
implementation, and `shared/<topic>` for shared or repository-wide work.
Keep platform changes separate. Land a required shared change independently
before bringing it into a platform task. Do not include unrelated formatting,
generated output, or another task's files.

### Commit messages and attribution

Directly authored commit subjects use:

```text
<type>(<optional-scope>): <Korean description>
```

Allowed types are `feat`, `fix`, `docs`, `test`, `perf`, `chore`, `style`,
`comment`, `ci`, `init`, `refactor`, `build`, and `revert`. The optional scope
names the affected domain, such as `release` or `commerce`; use `test/<domain>`
for a test scope. Prefer a concise Korean description that explains the
change. Other languages, longer descriptions, and sentence punctuation are
accepted. Do not append a pull request number to a directly authored subject.

An optional body explains why the change is needed and how behavior differs.
Line wrapping and prose style are author choices. Use `Resolves`, `Closes`, or
`Fixes` for an issue resolved by the commit; use `See also`, `Ref`, or
`Related to` for related work. Reference an issue in this source repository,
or qualify a public issue as
`sidey-app/SIDEY#<number>` so GitHub does not link the same number in the
wrong repository.

```text
fix(auth): API 응답에 접근할 수 없는 문제 수정

CORS 설정이 없어 브라우저가 API 응답 접근을 차단합니다.
요청과 응답에 CORS 헤더를 추가합니다.

Resolves: #273
See also: sidey-app/SIDEY#266
```

When Codex authors a new commit, preserve the human Git author and include
`Co-authored-by: codex <codex@openai.com>` exactly once. Install the tracked
hook once per clone; it also covers linked worktrees:

```shell
python3 scripts/skills/commit/setup_codex_attribution.py
```

If another hook manager owns the hook, connect the tracked hook through that
manager. If automatic attribution is unavailable, add the trailer once to the
new commit. Do not rewrite existing history to add attribution.

Before committing, inspect the complete worktree and task-base diff, stage
only explicit task paths, and review the staged diff. Validate the complete
message with `scripts/skills/commit/validate_commit_message.py`; do not
bypass a failed hook. The [$commit skill](.agents/skills/commit/SKILL.md)
provides the agent procedure for this work.

### Validation and pull requests

Run the checks required by the changed paths and review the full diff against
the task base. A failed, skipped, stale, or unavailable required check does
not pass. If source changes after validation, rerun affected checks. The task
workflow records the exact source and head it checked:

```shell
python3 scripts/skills/workflow.py check <task>
```

Start from the [general pull request template](.github/PULL_REQUEST_TEMPLATE/general.md)
when useful. A custom description is also accepted. Write the title using the
commit subject contract without a pull request number, and include concrete
change and validation evidence. Leave any failed or inapplicable check
unchecked. Review every changed path and confirm that the pull request
identifies the checked head.

Pushing a branch, creating a pull request, merging, releasing, uploading to a
store, and deploying to production are separate actions. Perform only the
actions authorized for the task. When branch publication and pull request
creation are authorized, use the checked workflow:

```shell
python3 scripts/skills/workflow.py publish <task> --title "<title>" --body-file <path>
```

The recorded local checks and exact reviewed head are required before integration.
GitHub Actions is disabled, so its archived workflows provide no validation.
Run platform and device checks separately and record their results in the pull
request. GitHub creates the squash
commit title and body from the repository's merge message settings. After
review feedback, make a focused follow-up commit and validate the new exact
head; an amended or rebuilt equivalent does not inherit the prior review or
check result. The [$create-pr skill](.agents/skills/create-pr/SKILL.md)
describes agent preparation and publication.

After `finish` verifies the exact merged commit and updates primary `main`, it
deletes that PR's remote branch at the checked head. It detaches the retained
task worktree at that head and deletes the local branch. An open PR or a
changed branch head blocks deletion. On later completed tasks, `finish` also
removes clean, verified-merged worktrees with no activity in the last 48 hours;
recent, dirty, locked, and open-PR worktrees stay in place.

After a Windows implementation has a clean checked head, create a local Setup
for manual testing before integration:

```shell
python3 scripts/skills/workflow.py test-installer <task>
```

The legacy current-main Windows Actions run option is unavailable while Actions
is disabled. The local Setup route copies the verified installer into
the maintainer's Downloads folder, then removes older SIDEY Windows Setup files
there. A failed build or changed source leaves the previous installers in
place.

## Security and privacy reports

Do not publish credentials, private messages, invite codes, personal data, or
security-sensitive reproduction details in a public issue. Use the private
contact method published in SIDEY's current policy pages for reports that
cannot be safely disclosed in public.
