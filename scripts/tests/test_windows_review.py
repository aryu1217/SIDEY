import sys
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "skills"))

import workflow as w  # noqa: E402


class WindowsReviewTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.primary = Path(self.temporary.name)
        self.commit = "a" * 40
        publish = self.primary / "build/windows/publish-smoke"
        (publish / "Runtime").mkdir(parents=True)
        (publish / "SIDEY.exe").write_bytes(b"launcher")
        (publish / "Runtime/SIDEY.Host.exe").write_bytes(b"host")

    def test_runs_local_windows_build_and_smoke_for_exact_main(self):
        with (
            patch.object(w, "os", SimpleNamespace(name="nt")),
            patch.object(w, "fetch_main", return_value=self.commit),
            patch.object(w, "head", return_value=self.commit),
            patch.object(w, "branch", return_value="main"),
            patch.object(w, "dirty_paths", return_value=[]),
            patch.object(w, "snapshot", return_value="source-snapshot"),
            patch.object(w, "run") as run,
        ):
            result = w.run_windows_local_review(self.primary, self.commit)

        self.assertEqual(result["main"], self.commit)
        self.assertEqual(result["source"], str(self.primary))
        self.assertIn("Windows", result["environment"])
        self.assertNotIn("GitHub Actions", result["environment"])
        self.assertEqual(result["source_snapshot"], "source-snapshot")
        self.assertEqual(len(result["launcher_sha256"]), 64)
        self.assertEqual(len(result["host_sha256"]), 64)
        self.assertEqual(run.call_count, 1)
        command = run.call_args.args
        self.assertEqual(command[0], self.primary)
        self.assertEqual(command[1], "powershell.exe")
        self.assertIn(
            str(self.primary / "scripts/windows/tests/Test-WindowsBuild.ps1"),
            command,
        )

    def test_rejects_non_windows_host_before_running_checks(self):
        with (
            patch.object(w, "os", SimpleNamespace(name="posix")),
            patch.object(w, "run") as run,
            self.assertRaises(w.WorkflowError),
        ):
            w.run_windows_local_review(self.primary, self.commit)
        run.assert_not_called()

    def test_failed_smoke_cannot_produce_a_pass(self):
        with (
            patch.object(w, "os", SimpleNamespace(name="nt")),
            patch.object(w, "fetch_main", return_value=self.commit),
            patch.object(w, "head", return_value=self.commit),
            patch.object(w, "branch", return_value="main"),
            patch.object(w, "dirty_paths", return_value=[]),
            patch.object(w, "snapshot", return_value="source-snapshot"),
            patch.object(w, "run", side_effect=w.WorkflowError("smoke failed")),
            self.assertRaisesRegex(w.WorkflowError, "smoke failed"),
        ):
            w.run_windows_local_review(self.primary, self.commit)

    def test_missing_published_host_cannot_produce_a_pass(self):
        (self.primary / "build/windows/publish-smoke/Runtime/SIDEY.Host.exe").unlink()
        with (
            patch.object(w, "os", SimpleNamespace(name="nt")),
            patch.object(w, "fetch_main", return_value=self.commit),
            patch.object(w, "head", return_value=self.commit),
            patch.object(w, "branch", return_value="main"),
            patch.object(w, "dirty_paths", return_value=[]),
            patch.object(w, "snapshot", return_value="source-snapshot"),
            patch.object(w, "run"),
            self.assertRaises(w.WorkflowError),
        ):
            w.run_windows_local_review(self.primary, self.commit)

    def test_rejects_stale_dirty_or_non_main_source_before_running_checks(self):
        for overrides in (
            {"current_main": "b" * 40},
            {"branch": "windows/task"},
            {"dirty": ["README.md"]},
        ):
            with self.subTest(overrides=overrides):
                with (
                    patch.object(w, "os", SimpleNamespace(name="nt")),
                    patch.object(
                        w, "fetch_main",
                        return_value=overrides.get("current_main", self.commit),
                    ),
                    patch.object(w, "head", return_value=self.commit),
                    patch.object(w, "branch", return_value=overrides.get("branch", "main")),
                    patch.object(w, "dirty_paths", return_value=overrides.get("dirty", [])),
                    patch.object(w, "snapshot", return_value="source-snapshot"),
                    patch.object(w, "run") as run,
                    self.assertRaises(w.WorkflowError),
                ):
                    w.run_windows_local_review(self.primary, self.commit)
                run.assert_not_called()

    def test_rejects_source_or_remote_change_during_local_checks(self):
        for changed in ("remote", "head", "dirty", "snapshot"):
            with self.subTest(changed=changed):
                remote = [self.commit, "b" * 40] if changed == "remote" else [self.commit] * 2
                head = [self.commit, "b" * 40] if changed == "head" else [self.commit] * 2
                dirty = [[], ["README.md"]] if changed == "dirty" else [[], []]
                source = ["source-snapshot", "changed"] if changed == "snapshot" else ["source-snapshot"] * 2
                with (
                    patch.object(w, "os", SimpleNamespace(name="nt")),
                    patch.object(w, "fetch_main", side_effect=remote),
                    patch.object(w, "head", side_effect=head),
                    patch.object(w, "branch", return_value="main"),
                    patch.object(w, "dirty_paths", side_effect=dirty),
                    patch.object(w, "snapshot", side_effect=source),
                    patch.object(w, "run") as run,
                    self.assertRaises(w.WorkflowError),
                ):
                    w.run_windows_local_review(self.primary, self.commit)
                run.assert_called_once()


if __name__ == "__main__":
    unittest.main()
