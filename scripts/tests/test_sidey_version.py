import json
from datetime import date
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


sys.path.insert(0, str(Path(__file__).parents[1]))
import sidey_version as versions


class SideyVersionTests(unittest.TestCase):
    def parse(self, head=2, yearweek="2640", windows_build=1, mac_build=37):
        return versions.parse_version_data({
            "schema": 2,
            "head": head,
            "yearweek": yearweek,
            "windowsBuild": windows_build,
            "macBuild": mac_build,
        })

    def test_platform_versions_share_head_and_week_but_have_independent_builds(self):
        version = self.parse(windows_build=12)
        self.assertEqual(version.windows_version, "2.2640.12")
        self.assertEqual(version.mac_version, "2.2640.37")
        self.assertEqual(version.msix_version, "2.2640.12.0")
        self.assertIn("<FileVersion>2.2640.12.0</FileVersion>",
                      versions.render_windows_props(version))
        self.assertIn("MARKETING_VERSION = 2.2640.37",
                      versions.render_macos_xcconfig(version))
        self.assertEqual(json.loads(versions.render_release_manifest("windows", version)), {
            "schema": 1,
            "platform": "windows",
            "channel": "production",
            "version": "2.2640.12",
            "windowsBuild": 12,
            "updateVersion": "2.2640.12",
            "releaseVersion": "2.2640.12",
            "msixVersion": "2.2640.12.0",
        })

    def test_iso_week_year_boundaries_and_invalid_weeks(self):
        self.assertEqual(self.parse(yearweek="1901").yearweek, "1901")
        self.assertEqual(self.parse(yearweek="1553").yearweek, "1553")
        for week in ("2654", "2553", "264", "26W0"):
            with self.subTest(week=week):
                with self.assertRaises(versions.VersionError):
                    self.parse(yearweek=week)

    def test_msix_limits_are_enforced_without_scaled_patch(self):
        self.assertEqual(self.parse(windows_build=65535).msix_version,
                         "2.2640.65535.0")
        for values in ({"head": 0}, {"head": 65536},
                       {"windows_build": 0}, {"windows_build": 65536},
                       {"mac_build": 0}):
            with self.subTest(values=values):
                with self.assertRaises(versions.VersionError):
                    self.parse(**values)

    def test_transition_preserves_platform_counters(self):
        previous = self.parse(windows_build=12, mac_build=37)
        versions.validate_transition(previous, self.parse(windows_build=13, mac_build=37))
        versions.validate_transition(previous, self.parse(windows_build=12, mac_build=38))
        for current in (self.parse(windows_build=11), self.parse(mac_build=36),
                        self.parse(head=1)):
            with self.subTest(current=current):
                with self.assertRaises(versions.VersionError):
                    versions.validate_transition(previous, current)
        versions.validate_transition(versions.LegacyVersion("2.1.0", 37), self.parse())

    def test_check_detects_stale_generated_mirror(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "release").mkdir()
            (root / "release/version.json").write_text(json.dumps({
                "schema": 2, "head": 2, "yearweek": "2640",
                "windowsBuild": 1, "macBuild": 37,
            }), encoding="utf-8")
            versions.write_platform("windows", root)
            versions.check_platform("windows", root)
            (root / "windows/Version.props").write_text("<Project />\n", encoding="utf-8")
            with self.assertRaisesRegex(versions.VersionError, "stale"):
                versions.check_platform("windows", root)

    def test_week_advance_updates_both_platforms_from_one_iso_date(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(["git", "init", "-q", "-b", "shared/week"], cwd=root,
                           check=True, capture_output=True)
            (root / "release").mkdir()
            (root / "release/version.json").write_text(json.dumps({
                "schema": 2, "head": 2, "yearweek": "2639",
                "windowsBuild": 8, "macBuild": 37,
            }), encoding="utf-8")
            (root / "release/app-store-localizations.json").write_text(json.dumps({
                "release": {"version": "2.2639.37", "build": 37},
            }), encoding="utf-8")
            self.assertEqual(versions.advance_yearweek(root, date(2026, 9, 30)), "2640")
            self.assertEqual(versions.load_version(root).windows_version, "2.2640.8")
            self.assertEqual(versions.load_version(root).mac_version, "2.2640.37")
            versions.check_platform("windows", root)
            versions.check_platform("macos", root)
            self.assertIn("2.2640.37", (root / "release/app-store-localizations.json")
                          .read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
