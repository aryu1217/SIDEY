from contextlib import redirect_stderr
from io import StringIO
import json
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).parents[1] / "skills"))
import verify_release_consistency as verification  # noqa: E402


class ReleaseConsistencyTests(unittest.TestCase):
    def setUp(self):
        temporary = TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.root_patch = patch.object(verification, "ROOT", self.root)
        self.root_patch.start()
        self.addCleanup(self.root_patch.stop)

    def published_release(self, version="2.1.0"):
        tag = f"windows-v{version}"
        return {
            "version": version,
            "url": f"https://github.com/sidey-app/SIDEY/releases/tag/{tag}",
            "publishedAt": "2026-09-28T07:21:23Z",
            "asset": {
                "url": (
                    f"https://github.com/sidey-app/SIDEY/releases/download/{tag}/"
                    f"SIDEY-Windows-x64-v{version}-Setup.exe"
                ),
                "sha256": "72d5b8b417bfadcabe1d83b41cdc09b75c1bfe97711d11a195abd821079dae40",
            },
        }

    def write_published_releases(self, entries):
        path = self.root / "website/src/data/release-notes/recent.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(entries), encoding="utf-8")

    def test_published_installer_can_be_older_than_staged_native_target(self):
        manifest = self.root / "release/windows.json"
        manifest.parent.mkdir()
        manifest.write_text(json.dumps({"version": "2.2640.3"}), encoding="utf-8")
        latest = self.published_release()
        previous = self.published_release("2.0.0")
        del previous["asset"]
        self.write_published_releases([previous, latest])
        self.assertEqual(verification.validate_published_windows_release(), latest)

    def test_published_installer_url_must_match_version_tag_and_repository(self):
        for url in (
            "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.0/"
            "SIDEY-Windows-x64-v2.1.0-Setup.exe",
            "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.1.0/"
            "SIDEY-Windows-x64-v2.0.0-Setup.exe",
            "https://example.com/SIDEY-Windows-x64-v2.1.0-Setup.exe",
        ):
            with self.subTest(url=url):
                entry = self.published_release()
                entry["asset"]["url"] = url
                self.write_published_releases([entry])
                with self.assertRaisesRegex(verification.ConsistencyError, "wrong installer URL"):
                    verification.validate_published_windows_release()

    def test_published_release_tag_url_must_match_version(self):
        entry = self.published_release()
        entry["url"] = "https://github.com/sidey-app/SIDEY/releases/tag/windows-v2.0.0"
        self.write_published_releases([entry])
        with self.assertRaisesRegex(verification.ConsistencyError, "wrong tag URL"):
            verification.validate_published_windows_release()

    def test_published_installer_digest_must_be_sha256(self):
        for digest in (None, "", "a" * 63, "g" * 64, "sha256:" + "a" * 64):
            with self.subTest(digest=digest):
                entry = self.published_release()
                entry["asset"]["sha256"] = digest
                self.write_published_releases([entry])
                with self.assertRaisesRegex(verification.ConsistencyError, "SHA-256 digest"):
                    verification.validate_published_windows_release()

    def test_published_arm64_asset_requires_its_architecture_and_valid_hash(self):
        entry = self.published_release()
        entry["arm64Asset"] = {
            "url": entry["asset"]["url"].replace("-x64-", "-arm64-"),
            "sha256": entry["asset"]["sha256"],
        }
        self.write_published_releases([entry])
        self.assertEqual(verification.validate_published_windows_release(), entry)
        entry["arm64Asset"]["url"] = entry["asset"]["url"]
        self.write_published_releases([entry])
        with self.assertRaisesRegex(verification.ConsistencyError, "wrong installer URL"):
            verification.validate_published_windows_release()
        entry["arm64Asset"]["url"] = entry["asset"]["url"].replace("-x64-", "-arm64-")
        entry["arm64Asset"]["sha256"] = "invalid"
        self.write_published_releases([entry])
        with self.assertRaisesRegex(verification.ConsistencyError, "SHA-256 digest"):
            verification.validate_published_windows_release()

    def test_latest_published_release_requires_asset_even_when_previous_has_one(self):
        latest = self.published_release()
        del latest["asset"]
        self.write_published_releases([self.published_release("2.0.0"), latest])
        with self.assertRaisesRegex(verification.ConsistencyError, "must contain installer metadata"):
            verification.validate_published_windows_release()

    def test_retired_pending_appcast_option_fails_safely(self):
        with patch.object(
            sys,
            "argv",
            ["verify_release_consistency.py", "--allow-pending-appcast"],
        ):
            with redirect_stderr(StringIO()) as error:
                self.assertEqual(verification.main(), 1)
        self.assertIn(
            "direct macOS release workflow is retired",
            error.getvalue(),
        )


if __name__ == "__main__":
    unittest.main()
