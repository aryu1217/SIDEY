from __future__ import annotations

import hashlib
import gzip
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts" / "pages"))
from prepare_release_metadata import (
    APP_STORE_URLS, prepare, read_incremental_manifest, read_manifest,
    replace_displayed_version, validate_repository, apply_arm64_downloads,
)


class PrepareReleaseMetadataTests(unittest.TestCase):
    def test_publishes_arm64_feed_separately_from_x64(self) -> None:
        with tempfile.TemporaryDirectory(prefix="SIDEY-ARM64-Pages-") as temporary:
            root = Path(temporary)
            site = self.write_landing_site(root)
            version = "2.2640.8"
            manifest = root / "windows.json"
            manifest.write_text(json.dumps({
                "schema": 1, "platform": "windows", "channel": "production",
                "version": version, "windowsBuild": 8, "updateVersion": version,
                "releaseVersion": version, "msixVersion": f"{version}.0",
            }), encoding="utf-8")
            x64 = root / f"SIDEY-Windows-x64-v{version}-Setup.exe"
            arm64 = root / f"SIDEY-Windows-arm64-v{version}-Setup.exe"
            x64.write_bytes(b"x64 installer")
            arm64.write_bytes(b"arm64 installer")
            output = root / "published"
            prepare(site, output, manifest, x64, windows_arm64_installer=arm64)
            html = (output / "en/index.html").read_text(encoding="utf-8")
            self.assertIn(f'data-windows-arm64-url="https://github.com/sidey-app/SIDEY/releases/download/windows-v{version}/{arm64.name}"', html)
            self.assertIn(hashlib.sha256(arm64.read_bytes()).hexdigest(), html)
            self.assertEqual(html.count('data-windows-arm64-url="https://github.com/sidey-app/SIDEY/releases/download/'), 1)
            self.assertIn(f'id="windows-arm64-download-action" href="https://github.com/sidey-app/SIDEY/releases/download/windows-v{version}/{arm64.name}"', html)
            self.assertNotIn('data-windows-arm64-download hidden', html)
            for architecture, installer in (("x64", x64), ("arm64", arm64)):
                filename = "windows-latest.json" if architecture == "x64" else "windows-arm64-latest.json"
                feed = json.loads((output / filename).read_text(encoding="utf-8"))
                self.assertTrue(feed["installer_url"].endswith(installer.name))
                self.assertEqual(feed["sha256"], hashlib.sha256(installer.read_bytes()).hexdigest())
            with self.assertRaisesRegex(ValueError, "ARM64 installer filename"):
                prepare(site, root / "invalid", manifest, x64, windows_arm64_installer=x64)

    @staticmethod
    def write_landing_site(directory: Path) -> Path:
        site = directory / "website"
        for locale, app_store_url in {**APP_STORE_URLS, "he": APP_STORE_URLS["en"]}.items():
            path = site / locale / "index.html"
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(
                '<a id="primary-download-action" href="#download">Download</a>'
                f'<a id="macos-download-action" href="{app_store_url}">Mac</a>'
                '<a id="windows-download-action" href="https://old.invalid/" data-windows-url="https://old.invalid/" data-windows-arm64-url="">Windows</a>'
                '<a id="windows-x64-download-action" href="https://old.invalid/">x64</a>'
                '<a id="windows-arm64-download-action" href="#download" data-windows-arm64-download hidden>ARM64</a>'
                '<div data-windows-arm64-download hidden>'
                '<code id="windows-arm64-download-sha256"></code></div>'
                '<code id="windows-download-sha256" data-release-platform="windows">'
                + "0" * 64 + '</code>'
                '<h3>SIDEY v<span data-release-version="windows">0.0.0</span></h3>',
                encoding="utf-8",
            )
        support = site / "support" / "index.html"
        support.parent.mkdir()
        support.write_text("<h1>Support</h1><p>Contact SIDEY.</p>", encoding="utf-8")
        return site

    def test_updates_discovered_landing_and_preserves_non_landing_page(self) -> None:
        with tempfile.TemporaryDirectory(prefix="SIDEY-Localized-Pages-") as temporary:
            root = Path(temporary)
            site = self.write_landing_site(root)
            # The local preview already includes a previously published ARM64 installer.
            for path in site.glob("*/index.html"):
                path.write_text(apply_arm64_downloads(path.read_text(), "https://old.invalid/arm64.exe", "b" * 64), encoding="utf-8")
            manifest = self.write_manifest(root)
            installer = root / "SIDEY-Windows-x64-v2.0.1-Setup.exe"
            installer.write_bytes(b"verified localized installer")
            output = root / "published"
            prepare(site, output, manifest, installer)
            html = (output / "he/index.html").read_text(encoding="utf-8")
            url = "https://github.com/sidey-app/SIDEY/releases/download/windows-v2.0.1/" + installer.name
            self.assertIn(f'data-windows-url="{url}"', html)
            self.assertIn(f'id="windows-download-action" href="{url}"', html)
            self.assertIn(f'id="windows-x64-download-action" href="{url}"', html)
            self.assertEqual(html.count(f'data-windows-url="{url}"'), 1)
            self.assertNotIn("https://old.invalid/arm64.exe", html)
            self.assertEqual(html.count('data-windows-arm64-url=""'), 1)
            self.assertEqual(html.count('data-windows-arm64-download hidden'), 2)
            self.assertIn('<code id="windows-arm64-download-sha256"></code>', html)
            self.assertIn(hashlib.sha256(installer.read_bytes()).hexdigest(), html)
            self.assertIn('id="primary-download-action" href="#download"', html)
            self.assertIn(f'id="macos-download-action" href="{APP_STORE_URLS["en"]}"', html)
            self.assertIn('<span data-release-version="windows">2.0.1</span>', html)
            self.assertNotIn("https://old.invalid/", html)
            self.assertEqual(
                (site / "support/index.html").read_bytes(),
                (output / "support/index.html").read_bytes(),
            )

    def test_discovered_malformed_landing_fails_validation(self) -> None:
        with tempfile.TemporaryDirectory(prefix="SIDEY-Malformed-Pages-") as temporary:
            root = Path(temporary)
            site = self.write_landing_site(root)
            landing = site / "he/index.html"
            html = landing.read_text(encoding="utf-8").replace(
                'id="macos-download-action"', 'id="removed-macos-download-action"'
            )
            landing.write_text(html, encoding="utf-8")
            manifest = self.write_manifest(root)
            installer = root / "SIDEY-Windows-x64-v2.0.1-Setup.exe"
            installer.write_bytes(b"verified installer")
            with self.assertRaisesRegex(ValueError, "macos-download-action"):
                prepare(site, root / "published", manifest, installer)

    def test_displayed_version_hook_is_optional_but_must_be_unique_and_valid(self) -> None:
        legacy = "<h3>SIDEY v2.0.0</h3>"
        self.assertEqual(replace_displayed_version(legacy, "windows", "2.0.1"), legacy)
        valid = '<span data-release-version="windows">2.0.0</span>'
        self.assertEqual(
            replace_displayed_version(valid, "windows", "2.0.1"),
            '<span data-release-version="windows">2.0.1</span>',
        )
        for invalid in (valid + valid, '<div data-release-version="windows">2.0.0</div>'):
            with self.subTest(html=invalid), self.assertRaisesRegex(ValueError, "one displayed"):
                replace_displayed_version(invalid, "windows", "2.0.1")

    @staticmethod
    def write_incremental(directory: Path, version: str = "2.0.1") -> Path:
        prefix = f"SIDEY-Windows-x64-v{version}"
        updater = directory / f"{prefix}-Update.exe"
        updater.write_bytes(b"update executable")
        files = []
        payload = bytearray()
        for name in ("SIDEY.exe", "Runtime/SIDEY.Host.exe",
                     "Runtime/SIDEY.UninstallHelper.exe", "Runtime/empty.txt"):
            content = b"" if name.endswith("empty.txt") else name.encode()
            compressed = gzip.compress(content, mtime=0)
            files.append({
                "path": name, "sha256": hashlib.sha256(content).hexdigest(),
                "size": len(content), "offset": len(payload), "length": len(compressed),
            })
            payload.extend(compressed)
        payload_path = directory / f"{prefix}-Payload.bin"
        payload_path.write_bytes(payload)
        manifest = directory / f"{prefix}-Update.json"
        manifest.write_text(json.dumps({
            "schema": 1, "version": version,
            "updater": {"name": updater.name, "size": updater.stat().st_size,
                        "sha256": hashlib.sha256(updater.read_bytes()).hexdigest()},
            "payload": {"name": payload_path.name, "size": len(payload),
                        "sha256": hashlib.sha256(payload).hexdigest()},
            "files": files,
        }), encoding="utf-8")
        return manifest

    def test_publishes_incremental_trust_root_and_preserves_full_installer(self) -> None:
        with tempfile.TemporaryDirectory(prefix="SIDEY-Incremental-Pages-") as temporary:
            root = Path(temporary)
            manifest = self.write_manifest(root)
            installer = root / "SIDEY-Windows-x64-v2.0.1-Setup.exe"
            installer.write_bytes(b"full installer")
            incremental = self.write_incremental(root)
            prepare(ROOT / "website" / "dist", root / "site", manifest, installer,
                    public_repository="sidey-app/SIDEY-public-staging",
                    windows_incremental_manifest=incremental)
            published = json.loads((root / "site/windows-latest.json").read_text(
                encoding="utf-8"))
            self.assertTrue(published["installer_url"].endswith(installer.name))
            self.assertEqual(published["incremental"], {
                "manifest_url": "https://github.com/sidey-app/SIDEY-public-staging/"
                                "releases/download/windows-v2.0.1/" + incremental.name,
                "sha256": hashlib.sha256(incremental.read_bytes()).hexdigest(),
            })
            self.assertEqual(published, json.loads(
                (root / "site/windows/update.json").read_text(encoding="utf-8")))

    def test_rejects_incomplete_or_modified_incremental_artifacts(self) -> None:
        for action in ("missing-updater", "missing-payload", "updater-hash", "payload-size"):
            with self.subTest(action=action), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                path = self.write_incremental(root)
                data = json.loads(path.read_text(encoding="utf-8"))
                field = "updater" if "updater" in action else "payload"
                artifact = root / data[field]["name"]
                if action.startswith("missing"):
                    artifact.unlink()
                elif action.endswith("hash"):
                    artifact.write_bytes(b"X" * artifact.stat().st_size)
                else:
                    artifact.write_bytes(artifact.read_bytes() + b"X")
                with self.assertRaises(ValueError):
                    read_incremental_manifest(path, "2.0.1")

    def test_rejects_invalid_incremental_file_contract(self) -> None:
        cases = {
            "traversal": {"path": "Runtime/../escape.dll"},
            "absolute": {"path": "/escape.dll"},
            "drive": {"path": "C:/escape.dll"},
            "backslash": {"path": "Runtime\\escape.dll"},
            "reserved": {"path": "Runtime/CON.txt"},
            "clock-reserved": {"path": "Runtime/CLOCK$"},
            "console-input-reserved": {"path": "Runtime/CONIN$.txt"},
            "console-output-reserved": {"path": "Runtime/CONOUT$"},
            "long-path": {"path": "Runtime/" + "a" * 241},
            "trailing-dot": {"path": "Runtime/file."},
            "duplicate": {"path": "sidey.EXE"},
            "uninstaller": {"path": "Uninstall.exe"},
            "hash": {"sha256": "bad"},
            "negative-size": {"size": -1},
            "boolean-size": {"size": True},
            "negative-offset": {"offset": -1},
            "empty-range": {"length": 0},
            "large-range": {"length": 128 * 1024 * 1024 + 1},
            "total-size": {"size": 2 * 1024 * 1024 * 1024},
            "unbounded": {"offset": 1000000},
            "overlap": {"offset": 0},
        }
        for name, mutation in cases.items():
            with self.subTest(case=name), tempfile.TemporaryDirectory() as temporary:
                path = self.write_incremental(Path(temporary))
                data = json.loads(path.read_text(encoding="utf-8"))
                data["files"][-1].update(mutation)
                path.write_text(json.dumps(data), encoding="utf-8")
                with self.assertRaises(ValueError):
                    read_incremental_manifest(path, "2.0.1")

    def test_rejects_incremental_resources_over_client_limits(self) -> None:
        for case in ("manifest-size", "file-count", "updater-size", "payload-size"):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as temporary:
                path = self.write_incremental(Path(temporary))
                data = json.loads(path.read_text(encoding="utf-8"))
                if case == "manifest-size":
                    data["padding"] = "x" * (4 * 1024 * 1024)
                elif case == "file-count":
                    data["files"] = data["files"] * 2501
                elif case == "updater-size":
                    data["updater"]["size"] = 128 * 1024 * 1024 + 1
                else:
                    data["payload"]["size"] = 2 * 1024 * 1024 * 1024 + 1
                path.write_text(json.dumps(data), encoding="utf-8")
                with self.assertRaises(ValueError):
                    read_incremental_manifest(path, "2.0.1")

    def test_rejects_wrong_version_artifact_name_and_missing_required_files(self) -> None:
        for case in ("version", "schema", "artifact-name", "required-file", "parent-file"):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as temporary:
                path = self.write_incremental(Path(temporary))
                data = json.loads(path.read_text(encoding="utf-8"))
                if case == "version":
                    data["version"] = "2.0.2"
                elif case == "schema":
                    data["schema"] = True
                elif case == "artifact-name":
                    data["updater"]["name"] = "../other.exe"
                elif case == "required-file":
                    data["files"].pop(0)
                else:
                    data["files"][-1]["path"] = "Runtime"
                path.write_text(json.dumps(data), encoding="utf-8")
                with self.assertRaises(ValueError):
                    read_incremental_manifest(path, "2.0.1")

    def test_headver_manifest_uses_one_windows_version(self) -> None:
        with tempfile.TemporaryDirectory(prefix="SIDEY-HeadVer-") as temporary:
            path = Path(temporary) / "windows.json"
            path.write_text(json.dumps({
                "schema": 1, "platform": "windows", "channel": "production",
                "version": "2.2640.8", "windowsBuild": 8,
                "updateVersion": "2.2640.8", "releaseVersion": "2.2640.8",
                "msixVersion": "2.2640.8.0",
            }), encoding="utf-8")
            self.assertEqual(read_manifest(path, "windows")["windowsBuild"], 8)
            changed = json.loads(path.read_text(encoding="utf-8"))
            changed["updateVersion"] = "2.2640.9"
            path.write_text(json.dumps(changed), encoding="utf-8")
            with self.assertRaises(ValueError):
                read_manifest(path, "windows")

    def test_headver_public_manifest_remains_readable_by_installed_clients(self) -> None:
        with tempfile.TemporaryDirectory(prefix="SIDEY-HeadVer-Pages-") as temporary:
            root = Path(temporary)
            manifest = root / "windows.json"
            version = "2.2640.8"
            manifest.write_text(json.dumps({
                "schema": 1, "platform": "windows", "channel": "production",
                "version": version, "windowsBuild": 8,
                "updateVersion": version, "releaseVersion": version,
                "msixVersion": f"{version}.0",
            }), encoding="utf-8")
            installer = root / f"SIDEY-Windows-x64-v{version}-Setup.exe"
            installer.write_bytes(b"verified installer fixture")
            prepare(ROOT / "website" / "dist", root / "site", manifest, installer)
            published = json.loads((root / "site/windows-latest.json").read_text(
                encoding="utf-8"))
            self.assertEqual(set(published), {
                "channel", "version", "tag", "installer_url", "sha256"})

    @staticmethod
    def write_manifest(
        directory: Path,
        *,
        product_version: str = "2.0.1",
        revision: int = 0,
        update_version: str = "2.0.1000",
        release_version: str = "2.0.1",
    ) -> Path:
        path = directory / "windows.json"
        path.write_text(json.dumps({
            "schema": 1,
            "platform": "windows",
            "channel": "production",
            "version": product_version,
            "windowsRevision": revision,
            "updateVersion": update_version,
            "releaseVersion": release_version,
            "msixVersion": f"{update_version}.0",
        }), encoding="utf-8")
        return path

    def test_prepares_localized_pages_and_windows_manifests(self) -> None:
        with tempfile.TemporaryDirectory(prefix="SIDEY-Pages-") as temporary:
            test_root = Path(temporary)
            release_dir = test_root / "release"
            release_dir.mkdir()
            windows_manifest_path = self.write_manifest(release_dir)
            windows_manifest = json.loads(windows_manifest_path.read_text(encoding="utf-8"))
            windows_installer = release_dir / (
                f"SIDEY-Windows-x64-v{windows_manifest['releaseVersion']}-Setup.exe"
            )
            shutil.copyfile(windows_manifest_path, windows_installer)
            output_dir = test_root / "site"

            prepare(
                ROOT / "website" / "dist",
                output_dir,
                windows_manifest_path,
                windows_installer,
            )

            windows_hash = hashlib.sha256(windows_installer.read_bytes()).hexdigest()
            published = json.loads(
                (output_dir / "windows-latest.json").read_text(encoding="utf-8")
            )
            self.assertEqual(published["sha256"], windows_hash)
            self.assertEqual(published["version"], "2.0.1")
            self.assertEqual(published["product_version"], "2.0.1")
            self.assertEqual(published["update_version"], "2.0.1000")
            self.assertEqual(published["update_tag"], "windows-v2.0.1")
            self.assertEqual(published["update_sha256"], windows_hash)
            self.assertEqual(
                published,
                json.loads((output_dir / "windows" / "update.json").read_text(encoding="utf-8")),
            )
            app_store_urls = {
                "ko": "https://apps.apple.com/kr/app/sidey/id6808528060?mt=12",
                "en": "https://apps.apple.com/us/app/sidey/id6808528060?mt=12",
                "ja": "https://apps.apple.com/jp/app/sidey/id6808528060?mt=12",
                "zh-hant": "https://apps.apple.com/tw/app/sidey/id6808528060?mt=12",
            }
            for locale, app_store_url in app_store_urls.items():
                html = (output_dir / locale / "index.html").read_text(encoding="utf-8")
                self.assertNotIn("macos-download-sha256", html)
                self.assertNotIn(".dmg", html)
                self.assertNotIn("brew-command", html)
                self.assertIn(f'href="{app_store_url}"', html)
                self.assertIn(
                    f'id="windows-download-sha256" data-release-platform="windows" data-checksum-text tabindex="0" aria-labelledby="windows-download-sha256-label">{windows_hash}</code>',
                    html,
                )

    def test_uses_an_explicit_staging_repository_in_download_urls(self) -> None:
        with tempfile.TemporaryDirectory(prefix="SIDEY-Pages-") as temporary:
            test_root = Path(temporary)
            release_dir = test_root / "release"
            release_dir.mkdir()
            windows_manifest_path = self.write_manifest(release_dir)
            windows_manifest = json.loads(windows_manifest_path.read_text(encoding="utf-8"))
            installer_name = (
                f"SIDEY-Windows-x64-v{windows_manifest['releaseVersion']}-Setup.exe"
            )
            windows_installer = release_dir / installer_name
            shutil.copyfile(windows_manifest_path, windows_installer)
            output_dir = test_root / "site"

            prepare(
                ROOT / "website" / "dist",
                output_dir,
                windows_manifest_path,
                windows_installer,
                "sidey-app/SIDEY-public-staging",
            )

            published = json.loads(
                (output_dir / "windows-latest.json").read_text(encoding="utf-8")
            )
            self.assertEqual(
                published["update_installer_url"],
                "https://github.com/sidey-app/SIDEY-public-staging/releases/"
                f"download/windows-v{windows_manifest['releaseVersion']}/{installer_name}",
            )
            html = (output_dir / "ko" / "index.html").read_text(encoding="utf-8")
            self.assertIn(f'href="{published["update_installer_url"]}"', html)
            self.assertIn(
                f'data-windows-url="{published["update_installer_url"]}"', html
            )

    def test_revision_release_preserves_product_bridge_for_legacy_clients(self) -> None:
        with tempfile.TemporaryDirectory(prefix="SIDEY-Pages-") as temporary:
            test_root = Path(temporary)
            release_dir = test_root / "release"
            release_dir.mkdir()
            manifest_path = self.write_manifest(
                release_dir,
                product_version="2.0.1",
                revision=1,
                update_version="2.0.1001",
                release_version="2.0.1001",
            )
            current_installer = release_dir / "SIDEY-Windows-x64-v2.0.1001-Setup.exe"
            current_installer.write_bytes(b"revision-one")
            bridge_installer = release_dir / "SIDEY-Windows-x64-v2.0.1-Setup.exe"
            bridge_installer.write_bytes(b"bridge")
            output_dir = test_root / "site"

            prepare(
                ROOT / "website" / "dist",
                output_dir,
                manifest_path,
                current_installer,
                windows_legacy_installer=bridge_installer,
            )

            published = json.loads(
                (output_dir / "windows-latest.json").read_text(encoding="utf-8")
            )
            self.assertEqual(published["version"], "2.0.1")
            self.assertEqual(published["tag"], "windows-v2.0.1")
            self.assertIn("windows-v2.0.1/SIDEY-Windows-x64-v2.0.1-Setup.exe",
                          published["installer_url"])
            self.assertEqual(published["update_version"], "2.0.1001")
            self.assertEqual(published["update_tag"], "windows-v2.0.1001")
            self.assertIn(
                "windows-v2.0.1001/SIDEY-Windows-x64-v2.0.1001-Setup.exe",
                published["update_installer_url"],
            )
            self.assertNotEqual(published["sha256"], published["update_sha256"])

    def test_revision_release_requires_product_bridge_installer(self) -> None:
        with tempfile.TemporaryDirectory(prefix="SIDEY-Pages-") as temporary:
            test_root = Path(temporary)
            release_dir = test_root / "release"
            release_dir.mkdir()
            manifest_path = self.write_manifest(
                release_dir,
                revision=1,
                update_version="2.0.1001",
                release_version="2.0.1001",
            )
            current_installer = release_dir / "SIDEY-Windows-x64-v2.0.1001-Setup.exe"
            current_installer.write_bytes(b"revision-one")

            with self.assertRaisesRegex(ValueError, "bridge installer is required"):
                prepare(
                    ROOT / "website" / "dist",
                    test_root / "site",
                    manifest_path,
                    current_installer,
                )

    def test_rejects_invalid_public_repository(self) -> None:
        for repository in ("SIDEY", "sidey-app/SIDEY/extra", "sidey app/SIDEY"):
            with self.subTest(repository=repository):
                with self.assertRaisesRegex(ValueError, "Invalid GitHub repository"):
                    validate_repository(repository)


if __name__ == "__main__":
    unittest.main()
