---
name: commit
description: Prepare and create focused local SIDEY commits from the current worktree using the repository message, staging, validation and attribution contracts. Use when asked to write a commit message or make a local commit; do not use for pushing, pull requests, merging or rewriting existing history.
---

# Commit

Read the root `AGENTS.md` and the internal source work and commit-message
sections of [CONTRIBUTING.md](../../../CONTRIBUTING.md#internal-source-work)
before acting. That guide owns the human-readable message, attribution and
review rules; the repository scripts own deterministic validation.

Determine whether the user wants only a proposed message or an actual local
commit. A message-only request must not change the index or repository.

For a local commit:

Follow the [logical task and Git boundaries](../remote-branch-workflow/SKILL.md#logical-task-and-git-boundaries).
Keep focused implementation, test, and documentation commits for the same
logical task on its existing branch. Changing commit type or invoking this
skill does not create a task, branch, or PR.

1. Confirm that the worktree and branch belong to the current task. Inspect
   `git status`, the unstaged diff, the staged diff, untracked files and the
   complete diff against the task base.
2. Choose one coherent commit boundary. Never include unrelated user or agent
   changes. Separate independent logical tasks under that contract; separate
   commits alone do not justify mixing unrelated work into the task's PR.
   Stop and report unresolved ownership conflicts.
3. Stage only explicit paths. Do not use `git add .`, broad globs, interactive
   staging or a command that can capture unknown files.
4. Review `git diff --cached` and verify that every staged path belongs to the
   intended commit. Check again for sensitive or generated files that the task
   does not require.
5. Write the subject, optional body and footers according to
   [CONTRIBUTING.md](../../../CONTRIBUTING.md#commit-messages-and-attribution).
   Check whether each issue reference belongs to the private source repository
   or needs a qualified public repository name. Validate the complete message
   with `scripts/skills/commit/validate_commit_message.py` before committing.
6. Preserve the human Git author. Install or use the repository attribution
   hook when Codex authored the change, and include the required trailer once.
7. Create the commit without bypassing hooks. If a hook fails, fix the cause
   and retry the commit; do not amend an earlier commit unless the user
   explicitly requested history rewriting.
8. Report the new commit ID, subject, committed paths, checks performed and any
   changes left in the worktree.

Never push, create a pull request, merge, tag, release or deploy as part of
this skill. Pull-request publication is a separate authorized operation handled
by `$create-pr`, with its own remote authorization check. It does not create a
new logical task, worktree, or branch.
