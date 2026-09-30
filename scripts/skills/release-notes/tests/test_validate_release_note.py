"""Tests for categorized public notes and exact private evidence endpoints."""
import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).parents[1] / "validate_release_note.py"
SPEC = importlib.util.spec_from_file_location("validate_release_note", SCRIPT)
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)

BASE = "windows-v1.3.1"
TARGET = "windows-v2.0.0"


def footer(target=TARGET):
    return "\n\n## 다른 언어로 보기\n\n" + "\n".join(VALIDATOR.language_footer(target)) + "\n"


def note(categories="## 버그 수정\n\n- 창 크기 변경 문제를 고쳤어요.", target=TARGET):
    return "프로그램 안정성을 개선했어요.\n\n" + categories + footer(target)


def evidence():
    return {
        "baseline": {"reference": BASE, "commit": "a" * 40},
        "target": {"reference": TARGET, "commit": "b" * 40},
        "integrations": [{"reference": "#108", "author": "patulus"}],
    }


class ValidateReleaseNoteTests(unittest.TestCase):
    def assert_invalid(self, source, message):
        with self.assertRaisesRegex(VALIDATOR.NoteError, message):
            VALIDATOR.validate_note(source, BASE, TARGET)

    def test_accepts_nonempty_category_subsets_and_preamble(self):
        for categories in [
            "## 새로운 기능\n\n- 기록에서 메시지를 보내요.",
            "## 개선 사항\n\n- 안내 문구를 다듬었어요.",
            "## 버그 수정\n\n- 종료 문제를 고쳤어요.",
            "## 새로운 기능\n\n- 기록에서 메시지를 보내요.\n\n"
            "## 개선 사항\n\n- 안내 문구를 다듬었어요.\n\n"
            "## 버그 수정\n\n- 종료 문제를 고쳤어요.",
        ]:
            with self.subTest(categories=categories):
                VALIDATOR.validate_note(note(categories), BASE, TARGET, evidence())
                VALIDATOR.validate_note("사용자 요청 문구\n\n---\n\n" + note(categories),
                                        BASE, TARGET, evidence())

    def test_rejects_empty_duplicate_out_of_order_and_old_categories(self):
        cases = [
            ("## 새로운 기능\n\n## 버그 수정\n\n- 수정했어요.", "Empty change"),
            ("## 버그 수정\n\n- 수정했어요.\n\n## 버그 수정\n\n- 고쳤어요.", "duplicated"),
            ("## 버그 수정\n\n- 수정했어요.\n\n## 새로운 기능\n\n- 추가했어요.", "ordered"),
            ("## 변경사항\n\n- 수정했어요.", "at least one"),
        ]
        for categories, message in cases:
            with self.subTest(categories=categories):
                self.assert_invalid(note(categories), message)

    def test_rejects_wrapped_change_and_missing_summary(self):
        self.assert_invalid(note("## 버그 수정\n\n- 수정했어요.\n  이어지는 문장"), "single-line")
        self.assert_invalid("## 버그 수정\n\n- 수정했어요." + footer(), "summary")

    def test_rejects_public_attribution_anywhere(self):
        for reference in ["PR 108", "#108", "commit 1a2b3c4", "@patulus", "a" * 40]:
            with self.subTest(reference=reference):
                self.assert_invalid(note() + "\n" + reference, "must not display")

    def test_rejects_source_links_and_compare(self):
        self.assert_invalid(note() + "\nhttps://github.com/SIDEY-APP/sidey-SOURCE/pull/108",
                            "must not link")
        self.assert_invalid(note() + "\nhttps://github.com/sidey-app/SIDEY/compare/a...b",
                            "must not claim")

    def test_accepts_public_issue_comment_permalink_in_installation_guidance(self):
        guidance = (
            "\n\n## 설치 전 확인\n\n"
            "설치 안내는 [GitHub 이슈](https://github.com/sidey-app/SIDEY/issues/4"
            "#issuecomment-5750233037)를 참고해 주세요.\n"
        )
        VALIDATOR.validate_note(note().replace(footer(), guidance + footer()), BASE, TARGET)

    def test_requires_exact_footer_version_locales_order_and_final_position(self):
        for source in [
            note().replace("#v2.0.0", "#v2.0.1"),
            note().replace("/zh-hant/", "/zh/"),
            note().replace("- [日本語]", "- [Japanese]"),
            note().replace("## 다른 언어로 보기", "## Languages"),
            note() + "\n## 설치\n\n설치하세요.",
            note().replace("- [English]", "- [한국어]"),
        ]:
            with self.subTest(source=source):
                self.assert_invalid(source, "language navigation|Language navigation")

    def test_historical_versions_link_archive_without_missing_anchor(self):
        old_target = "windows-v1.2.1"
        VALIDATOR.validate_note(note(target=old_target), "windows-v1.2.0", old_target)
        self.assertTrue(all("#v" not in line for line in VALIDATOR.language_footer(old_target)))
        self.assertTrue(all("#v2.0.0" in line for line in VALIDATOR.language_footer(TARGET)))
        self.assertTrue(all("#v2.40.0" in line for line in VALIDATOR.language_footer("windows-v2.40.0")))
        for target in ["main", "windows-v2x.0.0", "windows-v2", "windows-v2..0"]:
            with self.subTest(target=target):
                with self.assertRaisesRegex(VALIDATOR.NoteError, "exact"):
                    VALIDATOR.language_footer(target)

    def test_accepts_complete_current_locale_footer_and_checks_anchors(self):
        links = [
            f"[{locale}](https://sidey-app.github.io/SIDEY/{locale}/whats-new/windows/#v2.0.0)"
            for locale in VALIDATOR.WINDOWS_LOCALES
        ]
        source = note().replace(footer(), "\n\n## 다른 언어로 보기\n\n" + " | ".join(links) + "\n")
        VALIDATOR.validate_note(source, BASE, TARGET)
        self.assert_invalid(source.replace("#v2.0.0", "#v2.0.1", 1), "Language navigation")
        self.assert_invalid(source.replace("/de/whats-new/", "/en/whats-new/"), "Language navigation")

    def test_preserves_required_agpl_source_hash_only_in_license_notice(self):
        source_hash = "92088b671cebd073623701d15442b1ba3c8638e8"
        license_notice = (
            "\n\n## 소스 코드 및 라이선스\n\n"
            "GNU AGPLv3에 따른 정확한 소스는 첨부 파일에서 확인하세요. "
            f"(commit {source_hash})\n"
        )
        source = note().replace(footer(), license_notice + footer())
        VALIDATOR.validate_note(source, BASE, TARGET, evidence())
        for invalid in [
            note() + "\n" + source_hash,
            note().replace("프로그램 안정성을", f"{source_hash} 프로그램 안정성을"),
            source.replace("## 소스 코드 및 라이선스", "## 라이선스"),
            source.replace(source_hash, source_hash[:7]),
            source.replace(footer(), "\n\n## 참고\n\n" + source_hash + footer()),
            source.replace(source_hash, source_hash + " PR 108"),
            source.replace(source_hash, source_hash + " @patulus"),
        ]:
            with self.subTest(source=invalid):
                self.assert_invalid(invalid, "must not display")

    def test_preserves_exact_evidence_endpoint_validation(self):
        for key, message in [("baseline", "baseline"), ("target", "target")]:
            data = evidence()
            data[key]["reference"] = "different-tag"
            with self.assertRaisesRegex(VALIDATOR.NoteError, message):
                VALIDATOR.validate_note(note(), BASE, TARGET, data)
        for data in [[], {}, {"baseline": {}, "target": {}}, {
            "baseline": {"reference": BASE}, "target": {"reference": TARGET}
        }]:
            with self.subTest(data=data):
                with self.assertRaises(VALIDATOR.NoteError):
                    VALIDATOR.validate_note(note(), BASE, TARGET, data)
        # Authors are retained privately; public prose has no deterministic source reference.
        data = evidence()
        data["integrations"][0]["author"] = "another-author"
        VALIDATOR.validate_note(note(), BASE, TARGET, data)


if __name__ == "__main__":
    unittest.main()
