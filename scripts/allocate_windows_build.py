#!/usr/bin/env python3
"""Reserve a Windows HeadVer build number on the source Git remote.

Each successful reservation creates one immutable annotated tag. Failed builds
keep their reservation, so numbers are unique and increasing but may have gaps.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import subprocess
import sys
from uuid import uuid4


ROOT = Path(__file__).resolve().parents[1]
TAG_PREFIX = "refs/tags/windows-build/"
TAG_PATTERN = re.compile(r"^refs/tags/windows-build/([1-9][0-9]*)$")
MAX_BUILD = 65535
MAX_RETRIES = 20


class AllocationError(RuntimeError):
    """The remote could not safely reserve a build number."""


def git(repo: Path, *args: str, input_text: str | None = None) -> str:
    result = subprocess.run(
        ["git", *args], cwd=repo,
        input=input_text.encode("utf-8") if input_text is not None else None,
        capture_output=True, check=False,
    )
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).decode("utf-8", errors="replace").strip()
        raise AllocationError(f"git {' '.join(args[:2])} failed: {detail}")
    return result.stdout.decode("utf-8").strip()


def source_build(repo: Path) -> int:
    path = repo / "release/version.json"
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise AllocationError(f"cannot read {path}: {error}") from error
    if not isinstance(data, dict):
        raise AllocationError("release/version.json must contain an object")
    if data.get("schema") != 2:
        raise AllocationError("release/version.json must use HeadVer schema 2")
    build = data.get("windowsBuild")
    if type(build) is not int or not 1 <= build <= MAX_BUILD:
        raise AllocationError("windowsBuild must be an integer from 1 to 65535")
    return build


def remote_tags(repo: Path, remote: str) -> dict[int, str]:
    lines = git(repo, "ls-remote", "--refs", "--tags", remote, f"{TAG_PREFIX}*")
    result: dict[int, str] = {}
    for line in lines.splitlines():
        try:
            oid, ref = line.split("\t", 1)
        except ValueError as error:
            raise AllocationError(f"invalid remote tag listing: {line!r}") from error
        match = TAG_PATTERN.fullmatch(ref)
        if match is None or not re.fullmatch(r"[0-9a-f]{40,64}", oid):
            raise AllocationError(f"invalid Windows build tag: {ref!r}")
        result[int(match.group(1))] = oid
    return result


def verify_remote_source(repo: Path, remote: str, head: str) -> None:
    if git(repo, "status", "--porcelain", "--", "release/version.json"):
        raise AllocationError("release/version.json must match the committed source")
    branches = git(repo, "ls-remote", "--heads", remote)
    if not any(line.startswith(f"{head}\trefs/heads/") for line in branches.splitlines()):
        raise AllocationError("HEAD must already be the tip of a remote branch")


def annotated_tag_object(repo: Path, build: int, head: str) -> str:
    identity = git(repo, "var", "GIT_COMMITTER_IDENT")
    tag_name = f"windows-build/{build}"
    contents = (
        f"object {head}\n"
        "type commit\n"
        f"tag {tag_name}\n"
        f"tagger {identity}\n\n"
        f"SIDEY Windows build reservation {build}\n"
        f"source: {head}\n"
        f"created-utc: {datetime.now(timezone.utc).isoformat()}\n"
        f"nonce: {uuid4()}\n"
    )
    oid = git(repo, "mktag", input_text=contents)
    if not re.fullmatch(r"[0-9a-f]{40,64}", oid):
        raise AllocationError("git mktag returned an invalid object id")
    return oid


def allocate(repo: Path = ROOT, remote: str = "origin") -> int:
    if not remote or remote.startswith("-"):
        raise AllocationError("remote must name a configured Git remote")
    baseline = source_build(repo)
    git(repo, "remote", "get-url", remote)
    head = git(repo, "rev-parse", "--verify", "HEAD^{commit}")
    verify_remote_source(repo, remote, head)
    for _ in range(MAX_RETRIES):
        tags = remote_tags(repo, remote)
        build = max(baseline, *tags) + 1 if tags else baseline + 1
        if build > MAX_BUILD:
            raise AllocationError("Windows build exceeds the four-part version limit 65535")
        tag_oid = annotated_tag_object(repo, build, head)
        ref = f"{TAG_PREFIX}{build}"
        pushed = subprocess.run(
            ["git", "push", "--porcelain", remote, f"{tag_oid}:{ref}"],
            cwd=repo, capture_output=True, text=True, encoding="utf-8", check=False,
        )
        updated = remote_tags(repo, remote)
        if updated.get(build) == tag_oid:
            return build
        if pushed.returncode == 0:
            raise AllocationError(f"remote did not retain reserved tag {ref}")
        if build not in updated:
            detail = pushed.stderr.strip() or pushed.stdout.strip()
            raise AllocationError(f"cannot reserve {ref}: {detail}")
        # Another builder reserved the same number first; try its successor.
    raise AllocationError("too many concurrent Windows build reservations")


def apply_reservation(repo: Path, remote: str = "origin") -> int:
    """Reserve and stamp the source and Windows mirrors for an official build."""
    branch = git(repo, "symbolic-ref", "--quiet", "--short", "HEAD")
    if not branch.startswith("windows/"):
        raise AllocationError("official Windows build requires a windows/* task branch")
    paths = ("release/version.json", "release/windows.json", "windows/Version.props")
    if git(repo, "status", "--porcelain", "--", *paths):
        raise AllocationError("Windows version source and mirrors must be clean")
    try:
        from sidey_version import check_platform, write_platform
        check_platform("windows", repo)
    except (ImportError, RuntimeError) as error:
        raise AllocationError(f"Windows version mirrors are invalid: {error}") from error
    build = allocate(repo, remote)
    path = repo / "release/version.json"
    data = json.loads(path.read_text(encoding="utf-8"))
    data["windowsBuild"] = build
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8", newline="\n")
    write_platform("windows", repo)
    check_platform("windows", repo)
    return build


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=ROOT)
    parser.add_argument("--remote", default="origin")
    parser.add_argument("--apply", action="store_true",
                        help="stamp release/version.json and generated Windows mirrors")
    args = parser.parse_args()
    try:
        repo = args.repo.resolve()
        print(apply_reservation(repo, args.remote) if args.apply
              else allocate(repo, args.remote))
    except AllocationError as error:
        print(f"Windows build allocation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
