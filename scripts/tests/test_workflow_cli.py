import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_PATH = ROOT / "scripts" / "skills" / "workflow.py"
SKILL_SCRIPTS = WORKFLOW_PATH.parent
sys.path.insert(0, str(SKILL_SCRIPTS))


def load_workflow():
    spec = importlib.util.spec_from_file_location(
        "sidey_workflow_cli_tests",
        WORKFLOW_PATH,
    )
    if spec is None or spec.loader is None:
        raise RuntimeError("Cannot load the workflow script")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


workflow = load_workflow()


class WorkflowCommandSurfaceTests(unittest.TestCase):
    def help_text(self, *arguments):
        result = subprocess.run(
            [sys.executable, str(WORKFLOW_PATH), *arguments],
            cwd=ROOT,
            check=True,
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.stderr, "")
        return result.stdout

    def test_top_level_help_lists_every_supported_command(self):
        output = self.help_text("--help")

        self.assertIn(
            "{doctor,start,test-installer,verify-scope,sync,check,publish,finish,verify-windows,open}",
            output,
        )
        self.assertIn("--repo REPO", output)

    def test_command_help_preserves_public_options(self):
        expected_options = {
            "start": (
                "--platform {shared,macos,windows}",
                "--worktree WORKTREE",
                "--app {SIDEY,sidey-reals,windows}",
            ),
            "publish": (
                "--title TITLE",
                "--body-file BODY_FILE",
                "--push-remote PUSH_REMOTE",
            ),
            "verify-scope": (
                "--scope {macos,windows,web}",
                "--head HEAD",
                "--output OUTPUT",
            ),
            "check": ("--evidence EVIDENCE",),
            "open": (
                "--task TASK",
                "--preview PREVIEW",
                "--offline",
                "--scheme {SIDEY,sidey-reals}",
            ),
        }

        for command, options in expected_options.items():
            with self.subTest(command=command):
                output = self.help_text(command, "--help")
                for option in options:
                    self.assertIn(option, output)

        self.assertNotIn("required", self.help_text("start", "--help").split("--worktree WORKTREE", 1)[1].splitlines()[0])
        self.assertNotIn("--ci", self.help_text("check", "--help"))
        self.assertNotIn("--windows-run", self.help_text("finish", "--help"))
        self.assertIn("task", self.help_text("verify-windows", "--help").lower())

    def test_task_check_prints_indented_utf8_json(self):
        expected = {
            "status": "checked",
            "branch": "shared/readability",
        }

        with (
            patch.object(workflow, "root_at", return_value=ROOT),
            patch.object(workflow, "check_task", return_value=expected) as check,
            patch("sys.stdout", new_callable=io.StringIO) as stdout,
        ):
            result = workflow.main(
                [
                    "--repo",
                    str(ROOT),
                    "check",
                    "readability",
                ]
            )

        self.assertEqual(result, 0)
        check.assert_called_once_with(ROOT, "readability", [])
        self.assertEqual(json.loads(stdout.getvalue()), expected)
        self.assertTrue(stdout.getvalue().endswith("\n"))

    def test_script_reports_workflow_errors_on_stderr(self):
        result = subprocess.run(
            [
                sys.executable,
                str(WORKFLOW_PATH),
                "--repo",
                str(ROOT),
                "start",
                "INVALID",
                "--platform",
                "shared",
            ],
            cwd=ROOT,
            check=False,
            capture_output=True,
            text=True,
        )

        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stdout, "")
        self.assertTrue(result.stderr.startswith("workflow: "))
        self.assertIn("Task ID", result.stderr)

if __name__ == "__main__":
    unittest.main()
