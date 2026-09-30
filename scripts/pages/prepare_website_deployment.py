#!/usr/bin/env python3
"""Build a public website artifact without changing shipped Windows update feeds."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import shutil
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "publication"))
from validate_public_tree import validate_repository, validate_tree
from prepare_release_metadata import apply_arm64_downloads

REQUIRED_FEEDS = ("windows-latest.json", "windows/update.json")
OPTIONAL_FEEDS = ("windows/update-legacy.json", "windows-arm64-latest.json")
HASH = re.compile(r"[0-9a-f]{64}")
VERSION = re.compile(r"\d+\.\d+\.\d+")


def read_json(path: Path) -> object:
    return json.loads(path.read_text(encoding="utf-8"))


def version_parts(value: object) -> tuple[int, ...]:
    if not isinstance(value, str) or VERSION.fullmatch(value) is None:
        raise ValueError("Invalid published Windows version")
    return tuple(map(int, value.split(".")))


def release_assets(value: object) -> dict[str, dict[str, str]]:
    """Accept gh release view or REST API output, including optional bridge releases."""
    releases = value if isinstance(value, list) else [value]
    result: dict[str, dict[str, str]] = {}
    for release in releases:
        if not isinstance(release, dict):
            raise ValueError("Invalid GitHub release metadata")
        draft = release.get("isDraft", release.get("draft"))
        prerelease = release.get("isPrerelease", release.get("prerelease"))
        tag = release.get("tagName", release.get("tag_name"))
        if draft is not False or prerelease is not False:
            raise ValueError("Windows release must be published and stable")
        if not isinstance(tag, str) or not tag.startswith("windows-v"):
            raise ValueError("Invalid Windows release tag")
        version_parts(tag.removeprefix("windows-v"))
        if tag in result or not isinstance(release.get("assets"), list):
            raise ValueError("Missing assets or duplicate Windows release metadata")
        assets: dict[str, str] = {}
        for asset in release["assets"]:
            if not isinstance(asset, dict):
                raise ValueError("Invalid GitHub release asset")
            url = asset.get("browser_download_url", asset.get("url"))
            digest = asset.get("digest")
            if isinstance(url, str) and isinstance(digest, str):
                if "name" in asset and asset["name"] != url.rsplit("/", 1)[-1]:
                    raise ValueError("GitHub release asset name does not match its URL")
                if url in assets:
                    raise ValueError("Duplicate GitHub release asset URL")
                assets[url] = digest
        result[tag] = assets
    return result


def verify_download(
    version: object, tag: object, url: object, digest: object,
    releases: dict[str, dict[str, str]], repository: str, architecture: str = "x64",
) -> None:
    version_parts(version)
    expected_tag = f"windows-v{version}"
    expected_url = (
        f"https://github.com/{repository}/releases/download/{expected_tag}/"
        f"SIDEY-Windows-{architecture}-v{version}-Setup.exe"
    )
    if tag != expected_tag or url != expected_url:
        raise ValueError("Windows feed download URL or tag does not match its version")
    if not isinstance(digest, str) or HASH.fullmatch(digest) is None:
        raise ValueError("Windows feed installer SHA-256 is missing or invalid")
    if releases.get(expected_tag, {}).get(expected_url) != f"sha256:{digest}":
        raise ValueError("Windows feed installer differs from the stable GitHub release digest")


def verify_feed(
    feed: object, releases: dict[str, dict[str, str]], repository: str, architecture: str = "x64",
) -> tuple[str, str, str]:
    if not isinstance(feed, dict) or feed.get("channel") != "production":
        raise ValueError("Windows feed must describe the production channel")
    verify_download(feed.get("version"), feed.get("tag"), feed.get("installer_url"),
                    feed.get("sha256"), releases, repository, architecture)
    extended = {"product_version", "update_version", "update_tag",
                "update_installer_url", "update_sha256"}
    present = extended.intersection(feed)
    if present and present != extended:
        raise ValueError("Incomplete Windows bridge/update metadata")
    if present:
        if feed["product_version"] != feed["version"]:
            raise ValueError("Windows bridge product version differs from the feed version")
        if version_parts(feed["update_version"]) < version_parts(feed["version"]):
            raise ValueError("Windows update version cannot be older than the bridge version")
        verify_download(feed["update_version"], feed["update_tag"],
                        feed["update_installer_url"], feed["update_sha256"], releases,
                        repository, architecture)
        current = (feed["update_version"], feed["update_installer_url"], feed["update_sha256"])
    else:
        current = (feed["version"], feed["installer_url"], feed["sha256"])
    incremental = feed.get("incremental")
    if incremental is not None:
        if not isinstance(incremental, dict):
            raise ValueError("Invalid Windows incremental trust root")
        version = current[0]
        tag = f"windows-v{version}"
        url = (f"https://github.com/{repository}/releases/download/{tag}/"
               f"SIDEY-Windows-{architecture}-v{version}-Update.json")
        digest = incremental.get("sha256")
        if (incremental.get("manifest_url") != url or not isinstance(digest, str)
                or HASH.fullmatch(digest) is None
                or releases.get(tag, {}).get(url) != f"sha256:{digest}"):
            raise ValueError("Windows incremental trust root differs from the published release")
        for suffix in ("Update.exe", "Payload.bin"):
            artifact_url = url.removesuffix("Update.json") + suffix
            artifact_digest = releases.get(tag, {}).get(artifact_url, "")
            if not artifact_digest.startswith("sha256:") or HASH.fullmatch(artifact_digest[7:]) is None:
                raise ValueError("Windows incremental release requires its complete published trio")
    return current


def prepare(
    website_dir: Path, output_dir: Path, published_pages_dir: Path,
    release_snapshot: Path, github_release_json: Path,
    public_repository: str = "sidey-app/SIDEY",
) -> dict[str, object]:
    repository = validate_repository(public_repository)
    snapshot = read_json(release_snapshot)
    if not isinstance(snapshot, list) or any(not isinstance(item, dict) for item in snapshot):
        raise ValueError("Invalid published Windows release snapshot")
    retained = [item for item in snapshot if version_parts(item.get("version"))[0] >= 2]
    if not retained:
        raise ValueError("Published Windows release snapshot is empty")
    latest = max(retained, key=lambda item: version_parts(item["version"]))
    asset = latest.get("asset")
    if not isinstance(asset, dict):
        raise ValueError("Published Windows installer snapshot is missing")
    releases = release_assets(read_json(github_release_json))
    expected = (latest["version"], asset.get("url"), asset.get("sha256"))
    verify_download(expected[0], f"windows-v{expected[0]}", expected[1], expected[2],
                    releases, repository)
    feeds: dict[str, bytes] = {}
    arm64_download = None
    required_data: list[object] = []
    for relative in (*REQUIRED_FEEDS, *OPTIONAL_FEEDS):
        source = published_pages_dir / relative
        if not source.exists() and relative in OPTIONAL_FEEDS:
            continue
        if not source.is_file() or source.is_symlink():
            raise ValueError(f"Published Windows feed is missing or unsafe: {relative}")
        content = source.read_bytes()
        feed = json.loads(content)
        architecture = "arm64" if relative == "windows-arm64-latest.json" else "x64"
        current = verify_feed(feed, releases, repository, architecture)
        if architecture == "arm64":
            if current[0] != expected[0]:
                raise ValueError("Published ARM64 feed differs from the displayed Windows version")
            arm64_download = current
        if relative in REQUIRED_FEEDS:
            if current != expected:
                raise ValueError("Published Windows feed differs from the shipped website snapshot")
            required_data.append(feed)
        elif version_parts(current[0]) > version_parts(expected[0]):
            raise ValueError("Windows legacy feed cannot advertise a newer release")
        feeds[relative] = content
    if required_data[0] != required_data[1]:
        raise ValueError("Published Windows update feeds disagree")
    if output_dir.exists() and any(output_dir.iterdir()):
        raise ValueError("Website output directory must be empty")
    website_root = website_dir.resolve(strict=True)
    output_root = output_dir.resolve()
    if output_root == website_root or website_root in output_root.parents:
        raise ValueError("Website output directory must be outside the built website")
    shutil.copytree(website_root, output_root, symlinks=True, dirs_exist_ok=True)
    for relative, content in feeds.items():
        destination = output_root / relative
        if destination.is_symlink() or any(parent.is_symlink() for parent in destination.parents):
            raise ValueError("Website update feed destination must not be a symlink")
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(content)
    if arm64_download is not None:
        for path in output_root.glob("*/index.html"):
            html = path.read_text(encoding="utf-8")
            updated = apply_arm64_downloads(html, arm64_download[1], arm64_download[2])
            if updated != html:
                path.write_text(updated, encoding="utf-8", newline="\n")
    files = validate_tree(output_root, repository)
    return {"windowsVersion": expected[0], "windowsSHA256": expected[2],
            "preservedFeeds": list(feeds), "publicFileCount": len(files)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("website-dir", "output-dir", "published-pages-dir",
                 "release-snapshot", "github-release-json"):
        parser.add_argument(f"--{name}", type=Path, required=True)
    parser.add_argument("--public-repository", default="sidey-app/SIDEY")
    args = parser.parse_args()
    summary = prepare(args.website_dir, args.output_dir, args.published_pages_dir,
                      args.release_snapshot, args.github_release_json, args.public_repository)
    print(json.dumps(summary, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
