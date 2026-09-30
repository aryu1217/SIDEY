---
name: create-pr
description: Prepare or create a SIDEY pull request from the exact reviewed and validated task head using the repository template and workflow. Use for pull request titles, bodies, previews or publication; do not use for code review, merging, releases or deployments.
---

# Create Pull Request

Read the root `AGENTS.md` and the internal validation and pull request section
of [CONTRIBUTING.md](../../../CONTRIBUTING.md#validation-and-pull-requests),
then inspect the complete changed-path set. Start from
`.github/PULL_REQUEST_TEMPLATE/general.md` when useful and include concrete
change and validation evidence in any format. External asset contributions
are not accepted. This applies equally to agent-created and manual pull requests.

Separate local preparation from remote publication. Drafting a title, body or
preview does not authorize a push or pull request creation. Immediately before
the first remote mutation, confirm that the user request and established session
authorization explicitly cover publishing the branch and the intended PR
creation or update. A request to fix the same open PR can authorize its update;
it does not authorize another PR. If publication is not authorized, return the
prepared title and body without changing remote state.

Follow the [logical task and Git boundaries](../remote-branch-workflow/SKILL.md#logical-task-and-git-boundaries).
One logical task normally produces one pull request. Do not create separate PRs
merely because the branch contains implementation, tests, documentation,
configuration, workflow, localization, or packaging changes required to complete
the same task. Multiple focused commits do not imply multiple PRs. Reuse the
task-owned worktree and branch; invoking this skill is an operation within the
task, not a new task.

## Prepare

1. Confirm the task-owned worktree, branch prefix, integration base and current
   HEAD. Require a clean worktree and a committed task head.
2. Review the full diff and commit range against the current base. Reject
   unrelated paths, prohibited platform mixing and unreviewed changes.
3. Confirm the workflow checked record attests this exact HEAD. A passed scope
   may come from an earlier tested HEAD only when the workflow verifies identical
   tracked build/test inputs and preserves its provenance. Sync newer main and
   reuse unaffected scopes; rerun only changed scopes. A failed, skipped,
   unavailable or stale check blocks publication.
4. Write a title that passes
   `scripts/skills/commit/validate_commit_message.py --subject`.
   Do not add a pull request number to the title.
5. Describe the change and validation evidence. The general template is a
   starting point, not a required layout. Do not mark a failed or inapplicable
   check as passed.
6. Validate the body with the repository pull request validator and present
   the base, head, title, body and exact commit for review.

## Publish

Use `scripts/skills/workflow.py` for freshness, ownership, push and pull request
creation. Do not replace it with an ad hoc `git push` or `gh pr create` path.
After publication, read the pull request back and confirm its number, URL,
base, head commit, title and body.

Stop after the pull request is created or updated and verified. Do not merge
it, wait indefinitely for CI, release, upload or deploy. Those operations
require separate instructions.
GitHub owns the squash commit title and body through the repository's merge
message settings; do not pass custom squash subject or body overrides.

If an open PR already targets this logical task's owning repository, base, and
branch, update it by default. Make same-task follow-up fixes, tests, or
documentation in the existing worktree, commit, review the complete diff, rerun
required validation for the new exact HEAD, reusing only scopes whose input
fingerprints remain identical, and push under the applicable
authorization and workflow checks. Refresh the PR description when needed to
describe the complete final change. Do not create a replacement docs or test PR.

An existing PR may still show the previous head before the follow-up push;
that alone is not a task conflict. Verify that the remote head is the expected
prior task checkpoint, preserve unexpected remote changes, and confirm after
publication that the PR identifies the exact newly reviewed and validated HEAD.
A different task, repository, base, branch, or unexpected head is a conflict to
resolve, not a reason to silently create another PR.
