import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "skills"))

import workflow as w  # noqa: E402


class ScopeEvidenceTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.output = self.root / "evidence" / "web.json"
        self.head = "a" * 40
        self.base = "b" * 40
        self.snapshot = "source-snapshot"

    def evidence(self, scope="web", host="linux", **changes):
        return {
            "schema": 1,
            "scope": scope,
            "status": "passed",
            "head": self.head,
            "snapshot": self.snapshot,
            "base": self.base,
            "host": host,
            "time": 1,
            **changes,
        }

    def write_evidence(self, name, value):
        path = self.root / name
        path.write_text(json.dumps(value), encoding="utf-8")
        return path

    def test_verify_scope_records_exact_clean_source_after_checks(self):
        with (
            patch.object(w, "fetch_main", return_value=self.base) as fetch,
            patch.object(w, "head", return_value=self.head) as head,
            patch.object(w, "dirty_paths", return_value=[]) as dirty,
            patch.object(w, "is_ancestor", return_value=True) as ancestor,
            patch.object(w, "snapshot", return_value=self.snapshot) as snapshot,
            patch.object(w, "scope_input_fingerprint", return_value="scope-inputs"),
            patch.object(w, "run_scope_checks") as checks,
        ):
            result = w.verify_scope(self.root, "web", self.head, self.output)

        self.assertEqual(checks.call_args.args, (self.root, "web"))
        ancestor.assert_called_once_with(self.root, self.base, self.head)
        self.assertEqual(fetch.call_count, 1)
        self.assertGreaterEqual(head.call_count, 2)
        self.assertGreaterEqual(dirty.call_count, 2)
        self.assertGreaterEqual(snapshot.call_count, 2)
        self.assertEqual(result["evidence"], str(self.output.resolve()))
        recorded = json.loads(self.output.read_text(encoding="utf-8"))
        self.assertEqual(recorded["head"], self.head)
        self.assertEqual(recorded["base"], self.base)
        self.assertEqual(recorded["snapshot"], self.snapshot)
        self.assertEqual(recorded["host"], sys.platform)
        self.assertEqual(recorded["status"], "passed")
        self.assertEqual(recorded["inputs"], "scope-inputs")

    def test_verify_scope_rejects_wrong_head_dirty_source_or_stale_base(self):
        cases = (
            {"head": "c" * 40},
            {"dirty": ["README.md"]},
            {"ancestor": False},
        )
        for case in cases:
            with self.subTest(case=case):
                self.output.parent.mkdir(exist_ok=True)
                self.output.write_bytes(b"previous evidence")
                with (
                    patch.object(w, "fetch_main", return_value=self.base),
                    patch.object(w, "head", return_value=case.get("head", self.head)),
                    patch.object(w, "dirty_paths", return_value=case.get("dirty", [])),
                    patch.object(w, "is_ancestor", return_value=case.get("ancestor", True)),
                    patch.object(w, "run_scope_checks") as checks,
                    self.assertRaises(w.WorkflowError),
                ):
                    w.verify_scope(self.root, "web", self.head, self.output)
                checks.assert_not_called()
                self.assertEqual(self.output.read_bytes(), b"previous evidence")

    def test_failed_checks_or_source_drift_preserve_prior_evidence(self):
        self.output.parent.mkdir()
        self.output.write_bytes(b"previous evidence")
        for cause in ("checks", "head", "dirty", "snapshot"):
            with self.subTest(cause=cause):
                remote = [self.base, "c" * 40] if cause == "remote" else [self.base] * 2
                head = [self.head, "c" * 40] if cause == "head" else [self.head] * 2
                dirty = [[], ["README.md"]] if cause == "dirty" else [[], []]
                snapshot = [self.snapshot, "changed"] if cause == "snapshot" else [self.snapshot] * 2
                checks_error = w.WorkflowError("check failed") if cause == "checks" else None
                with (
                    patch.object(w, "fetch_main", side_effect=remote),
                    patch.object(w, "head", side_effect=head),
                    patch.object(w, "dirty_paths", side_effect=dirty),
                    patch.object(w, "is_ancestor", return_value=True),
                    patch.object(w, "snapshot", side_effect=snapshot),
                    patch.object(w, "run_scope_checks", side_effect=checks_error) as checks,
                    self.assertRaises(w.WorkflowError),
                ):
                    w.verify_scope(self.root, "web", self.head, self.output)
                checks.assert_called_once_with(self.root, "web")
                self.assertEqual(self.output.read_bytes(), b"previous evidence")

    def test_wrong_host_cannot_run_native_checks(self):
        with (
            patch.object(w.sys, "platform", "linux"),
            patch.object(w, "run") as run,
            self.assertRaisesRegex(w.WorkflowError, "macOS host"),
        ):
            w.run_scope_checks(self.root, "macos")
        run.assert_not_called()

    def test_web_checks_use_resolved_pnpm_executable(self):
        with (
            patch.object(w.shutil, "which", return_value="C:/tools/pnpm.cmd"),
            patch.object(w, "run") as run,
        ):
            w.run_scope_checks(self.root, "web")
        self.assertEqual(run.call_count, 2)
        self.assertEqual(run.call_args_list[0].args[:2], (self.root, "C:/tools/pnpm.cmd"))
        self.assertEqual(run.call_args_list[1].args[:2], (self.root, "C:/tools/pnpm.cmd"))

    def test_accepts_one_current_evidence_per_required_scope(self):
        paths = [
            self.write_evidence("macos.json", self.evidence("macos", "darwin")),
            self.write_evidence("windows.json", self.evidence("windows", "win32")),
            self.write_evidence("web.json", self.evidence("web", "linux")),
        ]
        result = w.validate_scope_evidence(
            paths, {"macos", "windows", "web"},
            self.head, self.snapshot, self.base,
        )
        self.assertEqual(set(result), {"macos", "windows", "web"})

    def test_rejects_missing_duplicate_and_unexpected_scope_evidence(self):
        web = self.write_evidence("web.json", self.evidence())
        duplicate = self.write_evidence("other-web.json", self.evidence())
        for paths, scopes in (
            ([], {"web"}),
            ([web], {"web", "windows"}),
            ([web, duplicate], {"web"}),
            ([web], {"windows"}),
        ):
            with self.subTest(paths=paths, scopes=scopes), self.assertRaises(w.WorkflowError):
                w.validate_scope_evidence(
                    paths, scopes, self.head, self.snapshot, self.base,
                )

    def test_rejects_stale_failed_and_wrong_host_evidence(self):
        invalid = (
            self.evidence(head="c" * 40),
            self.evidence(snapshot="changed"),
            self.evidence(base="c" * 40),
            self.evidence(schema=2),
            self.evidence(status="failed"),
            self.evidence("windows", "linux"),
            self.evidence("macos", "win32"),
        )
        for index, value in enumerate(invalid):
            with self.subTest(index=index):
                path = self.write_evidence(f"invalid-{index}.json", value)
                with self.assertRaises(w.WorkflowError):
                    w.validate_scope_evidence(
                        [path], {value["scope"]},
                        self.head, self.snapshot, self.base,
                    )


if __name__ == "__main__":
    unittest.main()
