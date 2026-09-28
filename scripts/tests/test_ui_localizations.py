import copy
import json
from pathlib import Path
import re
import sys
import tempfile
import unittest


SCRIPTS = Path(__file__).parents[1]
sys.path.insert(0, str(SCRIPTS))

import ui_localizations as tool  # noqa: E402


def flatten(value, prefix=""):
    output = {}
    for key, child in value.items():
        path = f"{prefix}.{key}" if prefix else key
        if isinstance(child, dict):
            output.update(flatten(child, path))
        else:
            output[path] = child
    return output


class UILocalizationTests(unittest.TestCase):
    def setUp(self):
        self.source = tool.read_source()
        self.internal_source = tool.read_internal_source()

    def test_canonical_source_is_complete_and_deterministic(self):
        tool.validate_source(self.source)
        tool.validate_internal_source(self.internal_source, self.source)
        first = tool.render_macos(self.source)
        second = tool.render_macos(self.source)
        self.assertEqual(first, second)
        mac = json.loads(first)
        self.assertEqual(mac["sourceLanguage"], "en")

        windows = tool.render_windows(self.source)
        self.assertEqual(
            set(windows),
            {f"{locale}.json" for locale in tool.CONSUMERS["windows"]["locales"].values()},
        )

    def test_macos_exports_only_configured_locales(self):
        configured = set(tool.CONSUMERS["macos"]["locales"].values())
        for rendered in (
            tool.render_macos(self.source),
            tool.render_internal_macos(self.internal_source),
        ):
            strings = json.loads(rendered)["strings"]
            exported = set(next(iter(strings.values()))["localizations"])
            self.assertEqual(exported, configured)

    def test_internal_strings_are_separate_from_production_catalogs(self):
        mac = json.loads(tool.render_macos(self.source))["strings"]
        internal_mac = json.loads(
            tool.render_internal_macos(self.internal_source)
        )["strings"]
        self.assertTrue(mac)
        self.assertTrue(internal_mac)
        self.assertTrue(set(mac).isdisjoint(internal_mac))

        windows = flatten(
            json.loads(tool.render_windows(self.source)["ko-KR.json"])
        )
        internal_windows = flatten(
            json.loads(
                tool.render_internal_windows(self.internal_source)["ko-KR.json"]
            )
        )
        self.assertTrue(windows)
        self.assertTrue(internal_windows)
        self.assertTrue(set(windows).isdisjoint(internal_windows))

    def test_metadata_and_macos_nested_prefix_values_stay_separate(self):
        metadata = tool.read_json(tool.ROOT / tool.SOURCE)
        self.assertEqual(
            set(metadata),
            {"schema", "canonical_locales", "consumers"},
        )

        prefixed = tool.flatten_locale_tree(
            {"store": {"price": {"$value": "가격", "short": "짧은 가격"}}},
            "fixture",
            "macos",
        )
        self.assertEqual(
            tuple(prefixed),
            ("store.price", "store.price.short"),
        )

        for consumer in ("shared", "windows"):
            with self.subTest(consumer=consumer), self.assertRaisesRegex(
                tool.LocalizationError,
                r"\$value is supported only for macOS keys",
            ):
                tool.flatten_locale_tree(
                    {"store": {"price": {"$value": "가격", "short": "짧은 가격"}}},
                    "fixture",
                    consumer,
                )

        with self.assertRaisesRegex(tool.LocalizationError, "invalid nested key"):
            tool.flatten_locale_tree(
                {"common.cancel": "취소"},
                "fixture",
                "shared",
            )

    def test_shared_messages_are_identical_for_both_consumers(self):
        mac = json.loads(tool.render_macos(self.source))["strings"]
        windows = {
            locale: flatten(json.loads(rendered))
            for locale, rendered in tool.render_windows(self.source).items()
        }
        for key, entry in self.source["shared"].items():
            for canonical, output in tool.CONSUMERS["macos"]["locales"].items():
                self.assertEqual(
                    mac[key]["localizations"][output]["stringUnit"]["value"],
                    entry["localizations"][canonical],
                )
            for canonical, output in tool.CONSUMERS["windows"]["locales"].items():
                self.assertEqual(
                    windows[f"{output}.json"][key],
                    entry["localizations"][canonical],
                )

    def test_placeholder_drift_is_rejected(self):
        invalid = copy.deepcopy(self.source)
        invalid["windows"]["history.retention.reload_failed"]["localizations"]["en"] = "History"
        with self.assertRaisesRegex(tool.LocalizationError, "placeholders differ"):
            tool.validate_source(invalid)

    def test_commerce_owned_character_name_is_rejected_from_client_source(self):
        invalid = copy.deepcopy(self.source)
        invalid["macos"]["character.pixel_chinchilla.display_name"] = {}
        with self.assertRaisesRegex(
            tool.LocalizationError,
            "macOS commerce keys must be owned by the commerce source",
        ):
            tool.validate_source(invalid)

    def test_malformed_placeholders_are_rejected(self):
        invalid = copy.deepcopy(self.source)
        for locale in tool.CONSUMERS["windows"]["locales"]:
            invalid["windows"]["history.retention.reload_failed"]["localizations"][locale] = "History {name}"
        with self.assertRaisesRegex(tool.LocalizationError, "malformed .NET placeholder"):
            tool.validate_source(invalid)

        invalid = copy.deepcopy(self.source)
        for locale in tool.CONSUMERS["windows"]["locales"]:
            invalid["windows"]["history.retention.reload_failed"]["localizations"][locale] = "History {broken"
        with self.assertRaisesRegex(tool.LocalizationError, "malformed .NET placeholder"):
            tool.validate_source(invalid)

        invalid = copy.deepcopy(self.source)
        for locale in tool.CONSUMERS["macos"]["locales"]:
            invalid["macos"]["message.send.failed"]["localizations"][locale]["stringUnit"]["value"] = "Failed: %s"
        with self.assertRaisesRegex(tool.LocalizationError, "malformed printf placeholder"):
            tool.validate_source(invalid)

        invalid = copy.deepcopy(self.source)
        invalid["macos"]["message.send.failed"]["localizations"]["ja"]["stringUnit"]["value"] = "失敗"
        with self.assertRaisesRegex(tool.LocalizationError, "placeholders differ"):
            tool.validate_source(invalid)

        invalid = copy.deepcopy(self.source)
        key = "history.retention_notice"
        for localization in invalid["macos"][key]["localizations"].values():
            for category in localization["variations"]["plural"].values():
                unit = category["stringUnit"]
                unit["value"] = unit["value"].replace("%lld", "%ld")
        korean_one = invalid["macos"][key]["localizations"]["ko"][
            "variations"
        ]["plural"]["one"]["stringUnit"]
        korean_one["value"] = korean_one["value"].replace("%ld", "%lld")
        with self.assertRaisesRegex(tool.LocalizationError, "plural placeholders differ"):
            tool.validate_source(invalid)

    def test_plural_shape_and_output_key_collisions_are_rejected(self):
        invalid = copy.deepcopy(self.source)
        plural = invalid["macos"]["history.retention_notice"]["localizations"]["en"]["variations"]["plural"]
        plural.pop("one")
        with self.assertRaisesRegex(tool.LocalizationError, "plural requires"):
            tool.validate_source(invalid)

        with self.assertRaisesRegex(tool.LocalizationError, "prefix collision"):
            catalog = {}
            tool.assign_nested(catalog, "a", "first")
            tool.assign_nested(catalog, "a.b", "second")

    def test_write_and_check_detect_stale_outputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            outputs = tool.expected_outputs(self.source, "all", root)
            tool.write_outputs(outputs)
            self.assertEqual(tool.check_outputs(outputs), [])
            first = next(iter(outputs))
            first.write_text("stale", encoding="utf-8")
            self.assertEqual(tool.check_outputs(outputs), [first])

    def test_duplicate_json_keys_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "duplicate.json"
            source.write_text('{"schema": 1, "schema": 2}', encoding="utf-8")
            with self.assertRaisesRegex(tool.LocalizationError, "duplicate JSON key: schema"):
                tool.read_json(source)

    def test_extra_windows_locale_is_stale(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            outputs = tool.expected_outputs(self.source, "windows", root)
            tool.write_outputs(outputs)
            extra = root / tool.CONSUMERS["windows"]["output"] / "stale.json"
            extra.write_text("{}\n", encoding="utf-8")
            self.assertEqual(tool.check_outputs(outputs), [extra])

    def test_locale_source_keys_are_functional_lower_snake_case(self):
        for root in (tool.LOCALE_ROOT, tool.INTERNAL_LOCALE_ROOT):
            for path in sorted((tool.ROOT / root).glob("*.json")):
                document = tool.read_json(path)

                def visit(value, key_path=()):
                    if not isinstance(value, dict):
                        return
                    for key, child in value.items():
                        if key != "$value":
                            self.assertRegex(
                                key,
                                r"^[a-z][a-z0-9_]*$",
                                f"{path}:{'.'.join(key_path + (key,))}",
                            )
                        visit(child, key_path + (key,))

                visit(document)

                windows = document.get("platforms", {}).get("windows", {})
                self.assertTrue(
                    tool.WINDOWS_NONFUNCTIONAL_ROOTS.isdisjoint(windows),
                    f"{path}: {tool.WINDOWS_NONFUNCTIONAL_ROOTS & set(windows)}",
                )

        catalogs = (
            ("production", tool.render_windows(self.source)),
            ("internal", tool.render_internal_windows(self.internal_source)),
        )
        for catalog, payloads in catalogs:
            for name, payload in payloads.items():
                for key in flatten(json.loads(payload)):
                    self.assertNotRegex(key, r"[A-Z]", f"{catalog}/{name}:{key}")
                    self.assertNotIn(
                        key.split(".")[0],
                        tool.WINDOWS_NONFUNCTIONAL_ROOTS,
                        f"{catalog}/{name}:{key}",
                    )

    def test_literal_windows_runtime_references_exist(self):
        production = set(self.source["shared"]) | set(self.source["windows"])
        production |= set(next(iter(tool.commerce_windows_overlays().values())))
        internal = set(self.internal_source["windows"])
        available = production | internal
        references = set()
        for path in sorted((tool.ROOT / "windows/src").rglob("*")):
            if path.suffix not in {".cs", ".xaml"}:
                continue
            source = path.read_text(encoding="utf-8")
            references.update(
                re.findall(r'I18n\.(?:Get|Format)\(\s*"([A-Za-z0-9_.]+)"', source)
            )
            references.update(
                re.findall(r"i18n:I18n Key=([A-Za-z0-9_.]+)", source)
            )
        self.assertEqual(sorted(references - available), [])

    def test_windows_platform_catalog_has_no_stale_keys(self):
        source_text = "\n".join(
            path.read_text(encoding="utf-8")
            for path in sorted((tool.ROOT / "windows/src").rglob("*"))
            if path.suffix in {".cs", ".xaml"}
        )
        # These labels ship before the Windows UI that consumes them. Keep the
        # exception limited to these staged handoffs.
        staged_windows_keys = {
            "settings.placement.monitor.display_name",
            "settings.placement.monitor.display_name_friendly",
            "settings.update.auto_download.title",
            "settings.update.available_action.description",
            "settings.update.available_version",
            "settings.update.download.action",
            "settings.update.install.action",
            "settings.update.notify.title",
            "settings.update.show_after.title",
            "tray.update.ready_to_install",
            "update.download.ready",
        }
        stale = sorted(
            key for key in self.source["windows"]
            if key not in source_text and key not in staged_windows_keys
        )
        self.assertEqual(stale, [])


if __name__ == "__main__":
    unittest.main()
