from __future__ import annotations

import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from prepare_website_deployment import prepare


class PrepareWebsiteDeploymentTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="SIDEY-Website-Deployment-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.site = self.root / "dist"
        for relative in ("index.html", "en/index.html", "ja/index.html", "ko/index.html", ".nojekyll"):
            path = self.site / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("", encoding="utf-8")
        self.pages = self.root / "published"
        self.output = self.root / "output"
        self.snapshot = self.root / "snapshot.json"
        self.release_file = self.root / "release.json"
        self.digest = "a" * 64
        self.url = ("https://github.com/sidey-app/SIDEY/releases/download/windows-v2.1.0/"
                    "SIDEY-Windows-x64-v2.1.0-Setup.exe")
        self.feed = {"channel": "production", "version": "2.1.0", "tag": "windows-v2.1.0",
                     "installer_url": self.url, "sha256": self.digest,
                     "product_version": "2.1.0", "update_version": "2.1.0",
                     "update_tag": "windows-v2.1.0", "update_installer_url": self.url,
                     "update_sha256": self.digest}
        self.snapshot_data = [
            {"version": "2.0.0"},
            {"version": "2.1.0", "asset": {"url": self.url, "sha256": self.digest}},
        ]
        self.release = {"tag_name": "windows-v2.1.0", "draft": False, "prerelease": False,
                        "assets": [{"name": "SIDEY-Windows-x64-v2.1.0-Setup.exe",
                                    "browser_download_url": self.url, "digest": f"sha256:{self.digest}"}]}
        self.write_json(self.snapshot, self.snapshot_data)
        self.write_json(self.release_file, self.release)
        self.write_json(self.pages / "windows-latest.json", self.feed)
        self.write_json(self.pages / "windows/update.json", self.feed)

    @staticmethod
    def write_json(path: Path, value: object) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

    def run_prepare(self) -> dict[str, object]:
        return prepare(self.site, self.output, self.pages, self.snapshot, self.release_file)

    def test_preserves_separate_arm64_feed_with_matching_release_digest(self) -> None:
        from test_prepare_release_metadata import PrepareReleaseMetadataTests
        self.site = PrepareReleaseMetadataTests.write_landing_site(self.root)
        for relative in ("index.html", ".nojekyll"):
            (self.site / relative).write_text("", encoding="utf-8")
        url = self.url.replace("-x64-", "-arm64-")
        feed = {"channel": "production", "version": "2.1.0", "tag": "windows-v2.1.0",
                "installer_url": url, "sha256": self.digest}
        self.release["assets"].append({"name": url.rsplit("/", 1)[1],
                                       "browser_download_url": url, "digest": f"sha256:{self.digest}"})
        self.write_json(self.release_file, self.release)
        self.write_json(self.pages / "windows-arm64-latest.json", feed)
        summary = self.run_prepare()
        self.assertIn("windows-arm64-latest.json", summary["preservedFeeds"])
        self.assertEqual((self.pages / "windows-arm64-latest.json").read_bytes(),
                         (self.output / "windows-arm64-latest.json").read_bytes())
        html = (self.output / "en/index.html").read_text(encoding="utf-8")
        self.assertIn(f'data-windows-arm64-url="{url}"', html)
        self.assertIn(self.digest, html)
        self.assertNotIn('data-windows-arm64-download hidden', html)

    def test_arm64_snapshot_requires_a_matching_public_asset(self) -> None:
        self.snapshot_data[-1]["arm64Asset"] = {
            "url": self.url.replace("-x64-", "-arm64-"), "sha256": self.digest,
        }
        self.write_json(self.snapshot, self.snapshot_data)
        with self.assertRaisesRegex(ValueError, "stable GitHub release digest"):
            self.run_prepare()

    def test_arm64_feed_cannot_point_to_x64_installer(self) -> None:
        self.write_json(self.pages / "windows-arm64-latest.json", self.feed)
        with self.assertRaisesRegex(ValueError, "download URL"):
            self.run_prepare()

    def test_arm64_download_version_must_match_displayed_windows_version(self) -> None:
        for version in ("2.0.0", "2.2.0"):
            with self.subTest(arm64_version=version):
                url = self.url.replace("-x64-", "-arm64-").replace("2.1.0", version)
                feed = {"channel": "production", "version": version,
                        "tag": f"windows-v{version}", "installer_url": url,
                        "sha256": self.digest}
                arm64_release = {"tag_name": f"windows-v{version}", "draft": False,
                                 "prerelease": False, "assets": [{
                                     "name": url.rsplit("/", 1)[1],
                                     "browser_download_url": url,
                                     "digest": f"sha256:{self.digest}"}]}
                # Each installer is a valid published asset; the displayed version is the mismatch.
                self.write_json(self.release_file, [self.release, arm64_release])
                self.write_json(self.pages / "windows-arm64-latest.json", feed)
                with self.assertRaisesRegex(ValueError, "ARM64 feed differs from the displayed Windows version"):
                    self.run_prepare()
                self.assertFalse(self.output.exists())

    def test_preserves_feed_bytes_and_optional_legacy_without_native_target(self) -> None:
        # Different whitespace is valid; each published file remains byte-for-byte intact.
        (self.pages / "windows/update.json").write_bytes(json.dumps(self.feed).encode() + b"\r\n")
        self.write_json(self.pages / "windows/update-legacy.json", self.feed)
        # This unrelated native build target must not become the publicly advertised release.
        self.write_json(self.root / "release/windows.json", {"version": "2.2640.999"})
        summary = self.run_prepare()
        self.assertEqual(summary["windowsVersion"], "2.1.0")
        self.assertEqual(summary["windowsSHA256"], self.digest)
        for relative in summary["preservedFeeds"]:
            self.assertEqual((self.pages / relative).read_bytes(),
                             (self.output / relative).read_bytes())
        self.assertEqual((self.site / "ko/index.html").read_bytes(),
                         (self.output / "ko/index.html").read_bytes())
        self.assertFalse((self.output / "release").exists())

    def test_missing_or_disagreeing_feeds_are_rejected(self) -> None:
        for action in ("missing", "different-extra-field", "partial-update-fields"):
            with self.subTest(action=action):
                changed = copy.deepcopy(self.feed)
                if action == "missing":
                    (self.pages / "windows/update.json").unlink()
                else:
                    if action == "different-extra-field":
                        changed["unexpected"] = "changed"
                    else:
                        del changed["update_sha256"]
                    self.write_json(self.pages / "windows/update.json", changed)
                with self.assertRaises(ValueError):
                    self.run_prepare()
                self.assertFalse(self.output.exists())
                self.write_json(self.pages / "windows/update.json", self.feed)

    def test_untrusted_download_metadata_is_rejected(self) -> None:
        for field, value in (("sha256", "b" * 64), ("sha256", ""),
                             ("installer_url", "https://example.com/malware.exe"),
                             ("tag", "windows-v2.0.0"), ("channel", "preview")):
            with self.subTest(field=field, value=value):
                changed = {**self.feed, field: value}
                self.write_json(self.pages / "windows-latest.json", changed)
                self.write_json(self.pages / "windows/update.json", changed)
                with self.assertRaises(ValueError):
                    self.run_prepare()
                self.assertFalse(self.output.exists())

    def test_stable_release_and_github_digest_are_required(self) -> None:
        for action in ("draft", "prerelease", "missing-status", "missing-digest", "wrong-digest"):
            with self.subTest(action=action):
                release = copy.deepcopy(self.release)
                if action == "draft":
                    release["draft"] = True
                elif action == "prerelease":
                    release["prerelease"] = True
                elif action == "missing-status":
                    del release["draft"]
                elif action == "missing-digest":
                    del release["assets"][0]["digest"]
                else:
                    release["assets"][0]["digest"] = "sha256:" + "b" * 64
                self.write_json(self.release_file, release)
                with self.assertRaises(ValueError):
                    self.run_prepare()
                self.assertFalse(self.output.exists())

    def test_snapshot_drift_and_missing_installer_are_rejected(self) -> None:
        for snapshot in ([], [{"version": "2.1.0"}],
                         [{"version": "2.1.0", "asset": {"url": self.url, "sha256": "b" * 64}}],
                         self.snapshot_data + [{"version": "2.2.0"}]):
            with self.subTest(snapshot=snapshot):
                self.write_json(self.snapshot, snapshot)
                with self.assertRaises(ValueError):
                    self.run_prepare()

    def test_bridge_and_gh_view_release_metadata_are_supported(self) -> None:
        legacy_version = "2.0.0"
        legacy_url = self.url.replace("2.1.0", legacy_version)
        legacy_hash = "c" * 64
        legacy_release = {"tagName": "windows-v2.0.0", "isDraft": False, "isPrerelease": False,
                          "assets": [{"url": legacy_url,
                                      "digest": "sha256:" + legacy_hash}]}
        self.write_json(self.release_file, [self.release, legacy_release])
        feed = {**self.feed, "version": legacy_version, "product_version": legacy_version,
                "tag": "windows-v2.0.0", "installer_url": legacy_url, "sha256": legacy_hash}
        for relative in ("windows-latest.json", "windows/update.json"):
            self.write_json(self.pages / relative, feed)
        summary = self.run_prepare()
        self.assertEqual(summary["windowsVersion"], "2.1.0")
        self.assertEqual(json.loads((self.output / "windows-latest.json").read_text()), feed)

    def test_incremental_manifest_trust_root_is_verified_and_preserved(self) -> None:
        url = self.url.replace("-Setup.exe", "-Update.json")
        feed = {**self.feed, "incremental": {"manifest_url": url, "sha256": "d" * 64}}
        for relative in ("windows-latest.json", "windows/update.json"):
            self.write_json(self.pages / relative, feed)
        with self.assertRaisesRegex(ValueError, "incremental"):
            self.run_prepare()
        self.release["assets"].append({"browser_download_url": url, "digest": "sha256:" + "d" * 64})
        self.write_json(self.release_file, self.release)
        with self.assertRaisesRegex(ValueError, "complete published trio"):
            self.run_prepare()
        for suffix in ("Update.exe", "Payload.bin"):
            self.release["assets"].append({"browser_download_url": url.removesuffix("Update.json") + suffix,
                                           "digest": "sha256:" + "e" * 64})
        self.write_json(self.release_file, self.release)
        self.run_prepare()
        self.assertEqual((self.pages / "windows-latest.json").read_bytes(),
                         (self.output / "windows-latest.json").read_bytes())

    def test_private_files_are_rejected_and_existing_output_is_not_overwritten(self) -> None:
        (self.site / "source.ts").write_text("private source", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Public tree validation failed"):
            self.run_prepare()
        original = self.output / "retain.txt"
        original.write_text("existing artifact", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "must be empty"):
            self.run_prepare()
        self.assertEqual(original.read_text(), "existing artifact")


if __name__ == "__main__":
    unittest.main()
