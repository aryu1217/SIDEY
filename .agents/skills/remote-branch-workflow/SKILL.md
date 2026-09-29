---
name: remote-branch-workflow
description: Start and maintain SIDEY source-changing work on an isolated task worktree whose branch is backed by the owning Git remote, with pushed recovery checkpoints throughout the task. Use before any implementation, documentation, asset, workflow, or other tracked-file change; do not use for read-only investigation or review.
---

# Remote-backed branch workflow

Prevent unfinished SIDEY work from existing only in a local or temporary
worktree. Apply this skill separately to every repository that the task will
change, including the private app source and backend repositories.

## Start before editing

1. Read the owning repository's `AGENTS.md` and contribution rules. Identify
   the correct repository, platform prefix, integration base, task name, and
   remote.
2. Inspect the primary checkout and existing worktrees. Preserve unrelated
   dirty changes and create a durable, task-owned worktree with the
   repository workflow command. Its default location is the primary checkout's
   `_workspace/<platform>-<task>` directory. Use an explicit
   `--worktree` only when that location cannot be used.
3. Immediately publish the new branch and set its upstream before changing a
   tracked file:

   ```shell
   git push -u origin <task-branch>
   ```

4. Verify both the local upstream and the remote ref. The remote branch must
   point to the task's starting commit before implementation begins.

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
