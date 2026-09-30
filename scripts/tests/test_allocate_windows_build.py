"""Exercise Windows build reservations against local bare Git remotes."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/allocate_windows_build.py"
sys.path.insert(0, str(ROOT / "scripts"))
from sidey_version import check_platform, write_platform


class WindowsBuildAllocationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="sidey-windows-build-")
        self.addCleanup(self.temp.cleanup)
        base = Path(self.temp.name)
        self.repo = base / "source"
        self.remote = base / "remote.git"
        self.repo.mkdir()
        self.env = {
            key: value for key, value in os.environ.items()
            if not key.startswith("GIT_")
        }
        self.env.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)
        self.git(self.repo, "init", "-q")
        self.git(self.repo, "config", "user.name", "SIDEY Test")
        self.git(self.repo, "config", "user.email", "sidey@example.test")
        (self.repo / "release").mkdir()
        self.set_source_build(1)
        write_platform("windows", self.repo)
        self.git(self.repo, "add", "release/version.json", "release/windows.json",
                 "windows/Version.props")
        self.git(self.repo, "commit", "-qm", "initial source")
        self.git(base, "init", "--bare", "-q", str(self.remote))
        self.git(self.repo, "remote", "add", "origin", str(self.remote))
        self.git(self.repo, "push", "-q", "origin", "HEAD:refs/heads/main")

    def git(self, repo, *args):
        return subprocess.run(
            ["git", *args], cwd=repo, env=self.env, capture_output=True,
            text=True, encoding="utf-8", check=True,
        ).stdout.strip()

    def set_source_build(self, build):
        (self.repo / "release/version.json").write_text(
            json.dumps({"schema": 2, "head": 2, "yearweek": "2640",
                        "windowsBuild": build, "macBuild": 37}) + "\n",
            encoding="utf-8",
        )

    def reserve(self, repo=None):
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--repo", str(repo or self.repo)],
            cwd=repo or self.repo, env=self.env, capture_output=True,
            text=True, encoding="utf-8", check=False,
        )

    def test_apply_stamps_source_and_both_mirrors(self):
        self.git(self.repo, "checkout", "-qb", "windows/build-test")
        self.git(self.repo, "push", "-q", "origin", "HEAD:refs/heads/windows/build-test")
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--repo", str(self.repo), "--apply"],
            cwd=self.repo, env=self.env, capture_output=True,
            text=True, encoding="utf-8", check=False,
        )
        self.assertEqual((result.returncode, result.stdout.strip()), (0, "2"),
                         result.stderr)
        source = json.loads((self.repo / "release/version.json").read_text(encoding="utf-8"))
        self.assertEqual(source["windowsBuild"], 2)
        check_platform("windows", self.repo)

    def test_reserves_global_successors_without_changing_source(self):
        before = (self.repo / "release/version.json").read_bytes()
        first = self.reserve()
        second = self.reserve()
        self.assertEqual((first.returncode, first.stdout.strip()), (0, "2"), first.stderr)
        self.assertEqual((second.returncode, second.stdout.strip()), (0, "3"), second.stderr)
        self.assertEqual((self.repo / "release/version.json").read_bytes(), before)
        tags = self.git(self.repo, "ls-remote", "--refs", "--tags", "origin",
                        "refs/tags/windows-build/*")
        self.assertIn("refs/tags/windows-build/2", tags)
        self.assertIn("refs/tags/windows-build/3", tags)
        self.assertEqual(self.git(self.repo, "status", "--porcelain"), "")

    def test_concurrent_clients_cannot_reuse_a_number(self):
        clone = Path(self.temp.name) / "other"
        self.git(Path(self.temp.name), "clone", "-q", str(self.remote), str(clone))
        self.git(clone, "checkout", "-q", "-b", "main", "origin/main")
        self.git(clone, "config", "user.name", "SIDEY Other Test")
        self.git(clone, "config", "user.email", "other@example.test")
        processes = [
            subprocess.Popen(
                [sys.executable, str(SCRIPT), "--repo", str(repo)],
                cwd=repo, env=self.env, stdout=subprocess.PIPE,
                stderr=subprocess.PIPE, text=True, encoding="utf-8",
            )
            for repo in (self.repo, clone)
        ]
        outcomes = [(process.returncode, out.strip(), err)
                    for process in processes
                    for out, err in [process.communicate(timeout=20)]]
        self.assertEqual(sorted((code, out) for code, out, _ in outcomes),
                         [(0, "2"), (0, "3")], outcomes)

    def test_missing_remote_fails_without_printing_a_number(self):
        self.git(self.repo, "remote", "remove", "origin")
        result = self.reserve()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stdout, "")
        self.assertIn("Windows build allocation failed", result.stderr)

    def test_four_part_version_limit_is_enforced(self):
        self.set_source_build(65535)
        self.git(self.repo, "add", "release/version.json")
        self.git(self.repo, "commit", "-qm", "raise source build")
        self.git(self.repo, "push", "-q", "origin", "HEAD:refs/heads/main")
        result = self.reserve()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stdout, "")
        self.assertIn("65535", result.stderr)

    def test_dirty_version_source_cannot_reserve(self):
        self.set_source_build(2)
        result = self.reserve()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stdout, "")
        self.assertIn("committed source", result.stderr)

    def test_unpushed_head_cannot_be_exposed_by_tag(self):
        self.git(self.repo, "commit", "-qm", "unpublished source", "--allow-empty")
        result = self.reserve()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stdout, "")
        self.assertIn("tip of a remote branch", result.stderr)


if __name__ == "__main__":
    unittest.main()
