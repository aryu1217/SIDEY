#!/usr/bin/env python3
"""Validate SIDEY's canonical Korean Windows release-note body."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import sys
from typing import Sequence

CHANGE_HEADINGS = ("## 새로운 기능", "## 개선 사항", "## 버그 수정")
LANGUAGE_HEADING = "## 다른 언어로 보기"
LANGUAGES = (("ko", "한국어"), ("en", "English"), ("ja", "日本語"), ("zh-hant", "繁體中文"))
PUBLIC_REFERENCES = re.compile(
    r"\bPR\s*#?\s*\d+|#\d+|(?<![\w.])@[A-Za-z0-9][A-Za-z0-9-]*",
    re.IGNORECASE
)


class NoteError(RuntimeError):
    """Raised when a release note violates the canonical format."""


def language_footer(target: str) -> list[str]:
    """Return exact version-linked language navigation."""
    match = re.fullmatch(r"windows-v([0-9]+(?:\.[0-9]+)+)", target)
    if match is None:
        raise NoteError("Target must be an exact windows-v<version> tag")
    version = match.group(1)
    anchor = f"#v{version}" if int(version.split(".")[0]) >= 2 else ""
    return [
        f"- [{label}](https://sidey-app.github.io/SIDEY/{locale}/whats-new/windows/{anchor})"
        for locale, label in LANGUAGES
    ]


def validate_evidence(evidence: object, baseline: str, target: str) -> None:
    """Validate range references; attribution is reviewed privately."""
    if not isinstance(evidence, dict):
        raise NoteError("Release evidence must be a JSON object")
    baseline_data, target_data = evidence.get("baseline"), evidence.get("target")
    if not isinstance(baseline_data, dict) or not isinstance(target_data, dict):
        raise NoteError("Release evidence is missing range endpoints")
    if baseline_data.get("reference") != baseline:
        raise NoteError("Release evidence baseline does not match --base")
    if target_data.get("reference") != target:
        raise NoteError("Release evidence target does not match --target")
    if not isinstance(evidence.get("integrations"), list):
        raise NoteError("Release evidence has no integrations list")


def validate_note(source: str, baseline: str, target: str, evidence: object | None = None) -> None:
    """Validate public format and exact source-range evidence when supplied."""
    normalized = source.replace("\r\n", "\n").replace("\r", "\n")
    lines = normalized.splitlines()
    lowered = normalized.casefold()
    if "github.com/sidey-app/sidey/compare/" in lowered:
        raise NoteError("Public release notes must not claim a source comparison URL")
    if "github.com/sidey-app/sidey-source" in lowered:
        raise NoteError("Public release notes must not link the private source repository")
    if PUBLIC_REFERENCES.search(normalized):
        raise NoteError("Public release notes must not display PR, commit or author attribution")
    in_license_notice = False
    for line in lines:
        if line.startswith("## "):
            in_license_notice = line == "## 소스 코드 및 라이선스"
        # The legally required corresponding-source notice may identify the exact
        # source with a full commit hash. It is not public change attribution.
        hash_pattern = r"\b[0-9a-f]{7,39}\b" if in_license_notice else r"\b[0-9a-f]{7,40}\b"
        if re.search(hash_pattern, line, re.IGNORECASE):
            raise NoteError("Public release notes must not display commit attribution outside the source license notice")
    headings = [(index, line) for index, line in enumerate(lines) if line in CHANGE_HEADINGS]
    if not headings:
        raise NoteError("Release note needs at least one change category")
    names = [line for _, line in headings]
    if len(set(names)) != len(names):
        raise NoteError("Change category headings must not be duplicated")
    if names != [line for line in CHANGE_HEADINGS if line in names]:
        raise NoteError("Change categories must be ordered: new, changed, fixed")
    if any(line in ("## 변경사항", "## 변경 사항", "## What's Changed") for line in lines):
        raise NoteError("Use the new, changed and fixed category headings")
    introduction = [
        line for line in lines[:headings[0][0]]
        if line.strip() and line != "---" and not line.startswith("#")
    ]
    if not introduction:
        raise NoteError("Release note needs a final-user summary before change categories")
    for index, category in headings:
        end = next((position for position in range(index + 1, len(lines))
                    if lines[position].startswith("## ")), len(lines))
        bullets = [line for line in lines[index + 1:end] if line.strip()]
        if not bullets:
            raise NoteError(f"Empty change category must be omitted: {category}")
        if any(not re.fullmatch(r"- \S.*", line) for line in bullets):
            raise NoteError("Every change must be a single-line bullet")
    footer_indexes = [index for index, line in enumerate(lines) if line == LANGUAGE_HEADING]
    if len(footer_indexes) != 1:
        raise NoteError("Release note must end with one language navigation section")
    footer_index = footer_indexes[0]
    if footer_index <= headings[-1][0]:
        raise NoteError("Language navigation must follow all change categories")
    footer = [line for line in lines[footer_index + 1:] if line.strip()]
    if footer != language_footer(target):
        raise NoteError("Language navigation must contain four exact version-linked website URLs")
    if evidence is not None:
        validate_evidence(evidence, baseline, target)


def parser() -> argparse.ArgumentParser:
    """Build the command-line parser."""
    command = argparse.ArgumentParser(description=__doc__)
    command.add_argument("path", type=Path, help="Canonical release-note file")
    command.add_argument("--base", required=True, help="Previous platform tag")
    command.add_argument("--target", required=True, help="Target platform tag")
    command.add_argument("--evidence", type=Path,
                         help="Collector JSON for exact endpoints; review private attribution separately")
    return command


def main(arguments: Sequence[str] | None = None) -> int:
    """Run the validator CLI."""
    options = parser().parse_args(arguments)
    try:
        source = options.path.read_text(encoding="utf-8")
        evidence = json.loads(options.evidence.read_text(encoding="utf-8")) if options.evidence else None
        validate_note(source, options.base, options.target, evidence)
    except (OSError, UnicodeError, json.JSONDecodeError, NoteError) as error:
        print(f"release note error: {error}", file=sys.stderr)
        return 1
    print(f"Release note format is valid: {options.path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
