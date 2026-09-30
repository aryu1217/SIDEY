---
name: remote-branch-workflow
description: Start and maintain SIDEY source-changing work on an isolated task worktree whose branch is backed by the owning Git remote, with pushed recovery checkpoints throughout the task. Use before any implementation, documentation, asset, workflow, or other tracked-file change; do not use for read-only investigation or review.
---

# Remote-backed branch workflow

Prevent unfinished SIDEY work from existing only in a local or temporary
worktree. Apply this skill separately to every repository that the task will
change, including the private app source and backend repositories.

## Logical task and Git boundaries

A logical task is one user-requested feature, bug fix, or maintenance purpose.
It normally uses one task-owned worktree, branch, and pull request per owning
repository, subject to repository platform and ownership rules. Implementation,
tests, developer documentation, configuration, packaging metadata, build or
workflow changes, localization, and other changes directly needed to complete
or accurately explain that purpose belong to the same task.

- A skill boundary selects a capability within the task. Invoking another
  skill does not create a new logical task, worktree, branch, or pull request.
- A commit boundary selects a focused, reviewable slice of the task. Multiple
  `feat`, `test`, or `docs` commits may remain on the same branch and in one PR.
- A pull request boundary selects a logical change for review and integration.
  Split only for an independently complete purpose worth separate review,
  merge, or revert, an explicit user request for separate PRs, or enforced
  repository, platform, or ownership boundaries.

Do not split because skills or commit types differ, source and documentation
change together, documentation follows implementation, or a checkpoint is
pushed. Unrelated work does not belong in the task's PR; handle it as a separate
logical task or report it as outside scope. This contract applies to every
skill used within the task and does not expand authorization.

Classify actual paths using repository policy, not the skill or commit type.
For example, Windows implementation and its `windows/docs/**` guides can share
a Windows task branch. A required change to shared-owned documents still follows
the repository's separate shared-change rule; do not bypass ownership to keep
one PR. Different repositories each require their own branch and PR when
publication is authorized.

## Start or resume before editing

1. Read the owning repository's `AGENTS.md` and contribution rules. Identify
   the correct repository, platform prefix, integration base, task name, and
   remote.
2. Inspect the primary checkout, existing worktrees, and repository task state.
   If this logical task already owns a worktree and branch, reuse them for
   implementation, tests, documentation, and follow-up fixes, including an
   existing open PR. Reapplying this skill does not require `start` again.
   Verify ownership and preserve unrelated dirty changes; do not adopt another
   task's worktree. Create a new durable, task-owned worktree with the repository
   workflow command only when no reusable task worktree exists. Its default
   location is the primary checkout's `_workspace/<platform>-<task>` directory. Use an explicit
   `--worktree` only when that location cannot be used.
3. For a new branch, immediately publish it and set its upstream before changing a
   tracked file:

   ```shell
   git push -u origin <task-branch>
   ```

4. Verify both the local upstream and the remote ref. A new remote branch must
   point to the task's starting commit before implementation begins. For a
   resumed task, compare its recorded checkpoint and current local and remote
   heads; preserve pending work and resolve unexpected divergence before pushing.

SIDEY's repository owner requires this branch publication as part of ordinary
authorized source-changing work. It does not authorize a pull request, merge,
release, upload, deployment, or any other external action.

If the branch cannot be created or verified remotely, do not start the edit.
Report the failure and leave existing worktrees untouched.

## Keep work recoverable

Create a focused checkpoint commit and push it whenever a coherent slice is
complete, and always before:

- pausing or handing off unfinished work;
- ending a work session with changes present;
- deleting, pruning, replacing, or abandoning a worktree;
- running cleanup that can remove temporary directories or Git objects; or
- beginning a risky repository operation that could make recovery harder.

Use the repository's commit policy, attribution hook, and explicit-path
staging rules. Inspect the staged content for unrelated files, credentials,
private data, and generated output before each commit. Push the checked
checkpoint to the configured upstream. Do not use an untracked patch file,
build output, editor state, or Codex session log as the only backup.

If a push fails after edits exist, stop cleanup and branch deletion. Keep the
durable worktree intact and report its path, branch, local HEAD, uncommitted
paths, and the failed remote operation.

## Verify before handoff or cleanup

Record and compare:

```shell
git status --short --branch
git rev-parse HEAD
git rev-parse @{upstream}
git ls-remote --heads origin refs/heads/<task-branch>
```

The final local commit and remote branch head must match. Any intentional
uncommitted files must be named explicitly; otherwise the task worktree must
be clean. Only then may the normal pull request, integration, finish, or
cleanup workflow continue under its own authorization and checks.
