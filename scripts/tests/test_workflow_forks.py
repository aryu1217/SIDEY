import argparse
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_PATH = ROOT / "scripts" / "skills" / "workflow.py"
sys.path.insert(0, str(WORKFLOW_PATH.parent))


def load_workflow():
    spec = importlib.util.spec_from_file_location(
        "sidey_workflow_fork_tests",
        WORKFLOW_PATH,
    )
    if spec is None or spec.loader is None:
        raise RuntimeError("Cannot load the workflow script")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


workflow = load_workflow()


class ForkRemoteTests(unittest.TestCase):
    def test_main_remote_prefers_upstream_for_a_fork_clone(self):
        with patch.object(
            workflow,
            "git",
            side_effect=(
                "origin\nupstream\n",
                "https://github.com/sidey-app/SIDEY-source.git",
            ),
        ), patch.object(
            workflow,
            "run",
            return_value=json.dumps(
                {
                    "id": workflow.GITHUB_REPOSITORY_ID,
                    "full_name": "sidey-app/SIDEY-source",
                }
            ),
        ):
            self.assertEqual(workflow.main_remote(ROOT), "upstream")

    def test_main_remote_accepts_canonical_origin(self):
        with patch.object(
            workflow,
            "git",
            side_effect=(
                "origin\n",
                "git@github.com:sidey-app/SIDEY-source.git",
            ),
        ), patch.object(
            workflow,
            "run",
            return_value=json.dumps(
                {
                    "id": workflow.GITHUB_REPOSITORY_ID,
                    "full_name": "sidey-app/SIDEY-source",
                }
            ),
        ):
            self.assertEqual(workflow.main_remote(ROOT), "origin")

    def test_main_remote_rejects_fork_without_canonical_upstream(self):
        with (
            patch.object(
                workflow,
                "git",
                side_effect=(
                    "origin\n",
                    "https://github.com/outside-contributor/SIDEY.git",
                ),
            ),
            self.assertRaisesRegex(
                workflow.WorkflowError,
                "add it as upstream",
            ),
        ):
            workflow.main_remote(ROOT)

    def test_main_remote_ignores_misconfigured_upstream(self):
        with patch.object(
            workflow,
            "git",
            side_effect=(
                "origin\nupstream\n",
                "https://github.com/not-sidey/not-sidey.git",
                "https://github.com/sidey-app/SIDEY-source.git",
            ),
        ), patch.object(
            workflow,
            "run",
            return_value=json.dumps(
                {
                    "id": workflow.GITHUB_REPOSITORY_ID,
                    "full_name": "sidey-app/SIDEY-source",
                }
            ),
        ):
            self.assertEqual(workflow.main_remote(ROOT), "origin")

    def test_legacy_name_requires_the_canonical_source_repository_id(self):
        with patch.object(
            workflow,
            "git",
            side_effect=(
                "origin\n",
                "https://github.com/sidey-app/SIDEY.git",
            ),
        ), patch.object(
            workflow,
            "run",
            return_value=json.dumps(
                {
                    "id": workflow.GITHUB_REPOSITORY_ID,
                    "full_name": "sidey-app/SIDEY",
                }
            ),
        ):
            self.assertEqual(workflow.source_repository(ROOT), "sidey-app/SIDEY")

        with patch.object(
            workflow,
            "git",
            side_effect=(
                "origin\n",
                "https://github.com/sidey-app/SIDEY.git",
            ),
        ), patch.object(
            workflow,
            "run",
            return_value=json.dumps(
                {
                    "id": 1378388474,
                    "full_name": "sidey-app/SIDEY",
                }
            ),
        ), self.assertRaisesRegex(
            workflow.WorkflowError,
            "does not identify the canonical",
        ):
            workflow.main_remote(ROOT)

    def test_fetch_main_refreshes_the_selected_remote(self):
        with (
            patch.object(
                workflow,
                "main_remote",
                return_value="upstream",
            ),
            patch.object(
                workflow,
                "git",
                side_effect=("", "canonical-main-sha"),
            ) as git,
        ):
            result = workflow.fetch_main(ROOT)

        self.assertEqual(result, "canonical-main-sha")
        self.assertEqual(
            git.call_args_list[0].args[1:],
            (
                "fetch",
                "--no-tags",
                "upstream",
                "+refs/heads/main:refs/remotes/upstream/main",
            ),
        )

    def test_github_remote_urls_resolve_to_owner_and_repository(self):
        urls = (
            "https://github.com/outside-contributor/SIDEY.git",
            "ssh://git@github.com/outside-contributor/SIDEY.git",
            "git@github.com:outside-contributor/SIDEY.git",
        )
        for url in urls:
            with self.subTest(url=url), patch.object(
                workflow,
                "git",
                return_value=url,
            ):
                self.assertEqual(
                    workflow.github_repository_from_remote(ROOT, "origin"),
                    "outside-contributor/SIDEY",
                )

    def test_non_github_push_remote_is_rejected(self):
        with (
            patch.object(
                workflow,
                "git",
                return_value="/tmp/local-fork.git",
            ),
            self.assertRaisesRegex(
                workflow.WorkflowError,
                "must use a github.com URL",
            ),
        ):
            workflow.github_repository_from_remote(ROOT, "origin")

    def test_publish_head_rejects_stale_public_distribution_push_url(self):
        with (
            patch.object(
                workflow,
                "github_repository_from_remote",
                return_value="sidey-app/SIDEY",
            ),
            patch.object(
                workflow,
                "github_repository_metadata",
                return_value={
                    "id": 1378388474,
                    "full_name": "sidey-app/SIDEY",
                    "private": False,
                },
            ),
            self.assertRaisesRegex(
                workflow.WorkflowError,
                "canonical SIDEY source repository",
            ),
        ):
            workflow.publish_head(ROOT, "origin")

    def test_publish_head_accepts_private_source_fork(self):
        with (
            patch.object(
                workflow,
                "github_repository_from_remote",
                return_value="friend/SIDEY-source",
            ),
            patch.object(
                workflow,
                "github_repository_metadata",
                return_value={
                    "id": 987654321,
                    "full_name": "friend/SIDEY-source",
                    "private": True,
                    "parent": {"id": workflow.GITHUB_REPOSITORY_ID},
                },
            ),
            patch.object(
                workflow,
                "branch",
                return_value="shared/readability",
            ),
        ):
            self.assertEqual(
                workflow.publish_head(ROOT, "fork"),
                "friend:shared/readability",
            )

    def test_create_head_only_unqualifies_the_canonical_owner(self):
        self.assertEqual(
            workflow.create_head("sidey-app:shared/readability"),
            "shared/readability",
        )
        self.assertEqual(
            workflow.create_head(
                "outside-contributor:shared/readability"
            ),
            "outside-contributor:shared/readability",
        )


class ForkPullRequestTests(unittest.TestCase):
    def test_cleanup_pr_lookup_includes_other_base_branches(self):
        with patch.object(workflow, 'run', return_value='[]') as run:
            workflow.open_task_prs(
                ROOT, 'sidey-app:windows/old',
                repository='sidey-app/SIDEY-source', base=None,
            )
        command = run.call_args.args[1:]
        self.assertIn('head=sidey-app:windows/old', command)
        self.assertFalse(any(arg.startswith('base=') for arg in command))

    def test_task_prs_uses_owner_qualified_api_filter(self):
        pulls = [
            {
                "number": 42,
                "head": {
                    "sha": "checked-head",
                    "repo": {
                        "full_name": "outside-contributor/SIDEY"
                    },
                },
                "merged_at": None,
                "merge_commit_sha": None,
            }
        ]
        with patch.object(
            workflow,
            "run",
            return_value=json.dumps(pulls),
        ) as run:
            result = workflow.task_prs(
                ROOT,
                "outside-contributor:shared/readability",
                repository="sidey-app/SIDEY-source",
            )

        self.assertEqual(
            result,
            [
                {
                    "number": 42,
                    "headRefOid": "checked-head",
                    "isCrossRepository": True,
                    "mergeCommit": None,
                }
            ],
        )
        command = run.call_args.args[1:]
        self.assertIn("repos/sidey-app/SIDEY-source/pulls", command)
        self.assertIn(
            "head=outside-contributor:shared/readability",
            command,
        )

    def test_merged_task_prs_exclude_unmerged_closed_results(self):
        pulls = [
            {
                "number": 41,
                "head": {
                    "sha": "merged-head",
                    "repo": {
                        "full_name": "outside-contributor/SIDEY"
                    },
                },
                "merged_at": "2026-09-17T00:00:00Z",
                "merge_commit_sha": "merge-sha",
            },
            {
                "number": 42,
                "head": {
                    "sha": "closed-head",
                    "repo": {
                        "full_name": "outside-contributor/SIDEY"
                    },
                },
                "merged_at": None,
                "merge_commit_sha": None,
            },
        ]
        with patch.object(
            workflow,
            "run",
            return_value=json.dumps(pulls),
        ) as run:
            result = workflow.task_prs(
                ROOT,
                "outside-contributor:shared/readability",
                state="merged",
                repository="sidey-app/SIDEY-source",
            )

        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["mergeCommit"], {"oid": "merge-sha"})
        self.assertIn("state=closed", run.call_args.args[1:])

    def test_exact_task_pr_accepts_a_fork_at_the_current_head(self):
        pull_request = {
            "number": 42,
            "headRefOid": "checked-head",
            "isCrossRepository": True,
        }
        with patch.object(
            workflow,
            "head",
            return_value="checked-head",
        ):
            number = workflow.require_exact_task_pr(ROOT, [pull_request])

        self.assertEqual(number, "42")

    def test_publish_pushes_fork_and_targets_canonical_repository(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        body_path = Path(temporary.name) / "pull-request.md"
        body_path.write_text("validated body", encoding="utf-8")
        task = {
            "checked": {"base": "base", "head": "checked-head"},
            "status": "checked",
        }
        current_pull_request = {
            "number": 42,
            "headRefOid": "checked-head",
            "isCrossRepository": True,
            "mergeCommit": None,
        }
        arguments = argparse.Namespace(
            task="readability",
            title="refactor(scripts): improve readability",
            body_file=str(body_path),
            push_remote="fork",
        )
        commands = []

        def command_response(root, *command, **kwargs):
            commands.append(command)
            if command[:3] == ("gh", "pr", "view"):
                return json.dumps(
                    {
                        "title": arguments.title,
                        "body": "validated body",
                    }
                )
            return ""

        with (
            patch.object(workflow, "owned_task", return_value=task),
            patch.object(workflow, "dirty_paths", return_value=[]),
            patch.object(
                workflow,
                "source_repository",
                return_value="sidey-app/SIDEY-source",
            ),
            patch.object(workflow, "fetch_main", return_value="base"),
            patch.object(workflow, "attest"),
            patch.object(
                workflow,
                "changed_paths",
                return_value=["scripts/example.py"],
            ),
            patch.object(workflow, "validate_paths"),
            patch.object(
                workflow,
                "publish_head",
                return_value=(
                    "outside-contributor:shared/readability"
                ),
            ),
            patch.object(
                workflow,
                "task_prs",
                side_effect=([], [current_pull_request]),
            ),
            patch.object(
                workflow,
                "require_pr_body_file",
                return_value=body_path,
            ),
            patch.object(workflow, "require_valid_commit_text"),
            patch.object(workflow, "require_valid_pr"),
            patch.object(
                workflow,
                "branch",
                return_value="shared/readability",
            ),
            patch.object(
                workflow,
                "head",
                return_value="checked-head",
            ),
            patch.object(workflow, "git") as git,
            patch.object(workflow, "update_task") as update_task,
            patch.object(
                workflow,
                "run",
                side_effect=command_response,
            ),
        ):
            result = workflow.publish(ROOT, arguments)

        git.assert_called_once_with(
            ROOT,
            "push",
            "-u",
            "fork",
            "shared/readability",
        )
        create = next(
            command
            for command in commands
            if command[:3] == ("gh", "pr", "create")
        )
        self.assertEqual(
            create[create.index("--repo") + 1],
            "sidey-app/SIDEY-source",
        )
        self.assertEqual(
            create[create.index("--head") + 1],
            "outside-contributor:shared/readability",
        )
        published = update_task.call_args.args[2]["published"]
        self.assertEqual(
            published["head_ref"],
            "outside-contributor:shared/readability",
        )
        self.assertEqual(published["push_remote"], "fork")
        self.assertEqual(
            result,
            {
                "status": "published",
                "pr": "42",
                "head": "checked-head",
            },
        )


if __name__ == "__main__":
    unittest.main()
