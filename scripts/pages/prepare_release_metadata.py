#!/usr/bin/env python3
"""Prepare the verified SIDEY artifact served by GitHub Pages."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil


DEFAULT_PUBLIC_REPOSITORY = "sidey-app/SIDEY"
APP_STORE_URLS = {
    "ko": "https://apps.apple.com/kr/app/sidey/id6808528060?mt=12",
    "en": "https://apps.apple.com/us/app/sidey/id6808528060?mt=12",
    "ja": "https://apps.apple.com/jp/app/sidey/id6808528060?mt=12",
    "zh-hant": "https://apps.apple.com/tw/app/sidey/id6808528060?mt=12",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--website-dir", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--windows-release-manifest", type=Path, required=True)
    parser.add_argument("--windows-release-installer", type=Path, required=True)
    parser.add_argument("--windows-legacy-installer", type=Path)
    parser.add_argument(
        "--public-repository",
        default=DEFAULT_PUBLIC_REPOSITORY,
        help="Public GitHub owner/repository used in release download URLs.",
    )
    return parser.parse_args()


def validate_repository(repository: str) -> str:
    if re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository) is None:
        raise ValueError(f"Invalid GitHub repository: {repository}")
    return repository


def read_manifest(path: Path, platform: str) -> dict[str, object]:
    manifest = json.loads(path.read_text(encoding="utf-8"))
    version = str(manifest.get("version", ""))
    is_headver = "windowsBuild" in manifest
    if is_headver and "windowsRevision" in manifest:
        raise ValueError("Windows manifest cannot mix HeadVer build and legacy revision")
    extended_fields = {
        "windowsBuild" if is_headver else "windowsRevision",
        "updateVersion", "releaseVersion", "msixVersion"
    }
    present_extended_fields = extended_fields.intersection(manifest)
    if present_extended_fields and present_extended_fields != extended_fields:
        raise ValueError(
            f"release/{platform}.json must contain all Windows version fields together."
        )
    if present_extended_fields:
        update_version = str(manifest.get("updateVersion", ""))
        release_version = str(manifest.get("releaseVersion", ""))
        msix_version = str(manifest.get("msixVersion", ""))
        windows_revision = manifest.get("windowsBuild" if is_headver else "windowsRevision")
    else:
        update_version = version
        release_version = version
        msix_version = f"{version}.0"
        windows_revision = 0
    if (
        manifest.get("schema") != 1
        or manifest.get("platform") != platform
        or manifest.get("channel") != "production"
        or re.fullmatch(r"\d+\.\d+\.\d+", version) is None
        or re.fullmatch(r"\d+\.\d+\.\d+", update_version) is None
        or re.fullmatch(r"\d+\.\d+\.\d+", release_version) is None
        or msix_version != f"{update_version}.0"
        or type(windows_revision) is not int
        or (not 1 <= windows_revision <= 65535 if is_headver
            else not 0 <= windows_revision <= 999)
        or release_version != (version if is_headver or windows_revision == 0
                               else update_version)
        or (is_headver and update_version != version)
        or (is_headver and (
            len(version.split(".")[1]) != 4
            or int(version.split(".")[2]) != windows_revision))
    ):
        raise ValueError(
            f"release/{platform}.json must describe a stable production {platform} release."
        )
    manifest.update({
        "windowsBuild" if is_headver else "windowsRevision": windows_revision,
        "updateVersion": update_version,
        "releaseVersion": release_version,
        "msixVersion": msix_version,
    })
    return manifest


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def require_anchor(html: str, element_id: str, **attributes: str) -> None:
    lookaheads = [rf'(?=[^>]*\bid="{re.escape(element_id)}")']
    lookaheads.extend(
        rf'(?=[^>]*\b{re.escape(name)}="{re.escape(value)}")'
        for name, value in attributes.items()
    )
    if re.search("<a" + "".join(lookaheads) + "[^>]*>", html) is None:
        raise ValueError(f"Verified download link is missing: {element_id}")


def replace_anchor_attribute(
    html: str, element_id: str, attribute: str, value: str
) -> str:
    pattern = re.compile(
        rf'(<a(?=[^>]*\bid="{re.escape(element_id)}")[^>]*\b'
        rf'{re.escape(attribute)}=")[^"]*(")'
    )
    updated, replacements = pattern.subn(rf"\g<1>{value}\g<2>", html)
    if replacements != 1:
        raise ValueError(
            f"Expected one {attribute} attribute for verified link: {element_id}"
        )
    return updated


def replace_checksum(html: str, platform: str, digest: str) -> str:
    element_id = f"{platform}-download-sha256"
    pattern = re.compile(
        rf'(<code(?=[^>]*\bid="{re.escape(element_id)}")'
        rf'(?=[^>]*\bdata-release-platform="{platform}")[^>]*>)[^<]*(</code>)'
    )
    updated, replacements = pattern.subn(rf"\g<1>{digest}\g<2>", html)
    if replacements == 0:
        raise ValueError(f"Release SHA-256 field is missing: {platform}")
    return updated


def prepare(
    website_dir: Path,
    output_dir: Path,
    windows_release_manifest: Path,
    windows_release_installer: Path,
    public_repository: str = DEFAULT_PUBLIC_REPOSITORY,
    windows_legacy_installer: Path | None = None,
) -> tuple[str, str]:
    website_dir = website_dir.resolve(strict=True)
    windows_release_manifest = windows_release_manifest.resolve(strict=True)
    windows_release_installer = windows_release_installer.resolve(strict=True)
    output_dir = output_dir.resolve()
    public_repository = validate_repository(public_repository)

    windows_manifest = read_manifest(windows_release_manifest, "windows")
    product_version = str(windows_manifest["version"])
    update_version = str(windows_manifest["updateVersion"])
    release_version = str(windows_manifest["releaseVersion"])
    release_name = f"SIDEY-Windows-x64-v{release_version}-Setup.exe"
    if windows_release_installer.name != release_name:
        raise ValueError(
            "Windows release installer filename does not match the public contract: "
            + release_name
        )
    if windows_legacy_installer is None:
        if release_version != product_version:
            raise ValueError(
                "A Product Version bridge installer is required for a Windows revision release."
            )
        windows_legacy_installer = windows_release_installer
    else:
        windows_legacy_installer = windows_legacy_installer.resolve(strict=True)
    legacy_name = f"SIDEY-Windows-x64-v{product_version}-Setup.exe"
    if windows_legacy_installer.name != legacy_name:
        raise ValueError(
            "Windows bridge installer filename does not match the Product Version contract: "
            + legacy_name
        )
    if output_dir.exists() and any(output_dir.iterdir()):
        raise ValueError(f"Pages output directory must be empty: {output_dir}")

    output_dir.mkdir(parents=True, exist_ok=True)
    shutil.copytree(website_dir, output_dir, dirs_exist_ok=True)
    update_hash = sha256(windows_release_installer)
    legacy_hash = sha256(windows_legacy_installer)
    update_url = (
        f"https://github.com/{public_repository}/releases/download/"
        f"windows-v{release_version}/{release_name}"
    )
    legacy_url = (
        f"https://github.com/{public_repository}/releases/download/"
        f"windows-v{product_version}/{legacy_name}"
    )
    published_windows_manifest = {
        "channel": "production",
        "version": product_version,
        "tag": f"windows-v{product_version}",
        "installer_url": legacy_url,
        "sha256": legacy_hash,
    }
    if "windowsBuild" not in windows_manifest:
        published_windows_manifest.update({
            "product_version": product_version,
            "update_version": update_version,
            "update_tag": f"windows-v{release_version}",
            "update_installer_url": update_url,
            "update_sha256": update_hash,
        })
    manifest_text = json.dumps(published_windows_manifest, indent=2) + "\n"
    for relative_path in ("windows-latest.json", "windows/update.json"):
        path = output_dir / relative_path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(manifest_text, encoding="utf-8", newline="\n")

    for locale, app_store_url in APP_STORE_URLS.items():
        relative_path = Path(locale) / "index.html"
        path = output_dir / relative_path
        if not path.is_file():
            raise ValueError(f"Localized landing page is missing: {relative_path.as_posix()}")
        html = path.read_text(encoding="utf-8")
        html = replace_anchor_attribute(
            html, "primary-download-action", "data-windows-url", update_url
        )
        html = replace_anchor_attribute(
            html, "windows-download-action", "href", update_url
        )
        require_anchor(
            html,
            "primary-download-action",
            href=app_store_url,
            **{"data-macos-url": app_store_url, "data-windows-url": update_url},
        )
        require_anchor(html, "macos-download-action", href=app_store_url)
        require_anchor(html, "windows-download-action", href=update_url)
        html = replace_checksum(html, "windows", update_hash)
        path.write_text(html, encoding="utf-8", newline="\n")

    return release_version, update_hash


def main() -> int:
    args = parse_args()
    windows_version, windows_hash = prepare(
        args.website_dir,
        args.output_dir,
        args.windows_release_manifest,
        args.windows_release_installer,
        args.public_repository,
        args.windows_legacy_installer,
    )
    print("ReleaseMetadataPrepared=true")
    print(f"WindowsVersion={windows_version}")
    print(f"WindowsSHA256={windows_hash}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
