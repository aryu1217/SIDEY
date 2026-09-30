#!/usr/bin/env python3
"""Validate SIDEY's authoritative platform release manifests and their mirrors."""

from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from sidey_version import (  # noqa: E402
    VersionError,
    check_platform,
    load_version,
)


ROOT = Path(__file__).resolve().parents[2]
SEMVER = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
class ConsistencyError(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ConsistencyError(message)


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def load_manifest(platform: str) -> dict[str, object]:
    path = ROOT / "release" / f"{platform}.json"
    data = json.loads(path.read_text(encoding="utf-8"))
    required = {"schema", "platform", "channel", "version"}
    windows_generated = (ROOT / "windows" / "Version.props").is_file()
    if platform == "macos":
        required.add("build")
    elif windows_generated:
        required.update({
            "windowsBuild",
            "updateVersion",
            "releaseVersion",
            "msixVersion",
        })
    require(set(data) == required, f"{path} must contain exactly {sorted(required)}")
    require(data["schema"] == 1, f"{path} has an unsupported schema")
    require(data["platform"] == platform, f"{path} has the wrong platform")
    expected_channel = "appstore" if platform == "macos" else "production"
    require(data["channel"] == expected_channel,
            f"{path} must use the {expected_channel} channel")
    require(
        isinstance(data["version"], str) and SEMVER.fullmatch(data["version"]),
        f"{path} must contain a stable semantic version",
    )
    if platform == "macos":
        require(
            isinstance(data["build"], int) and data["build"] > 0,
            f"{path} must contain a positive numeric build",
        )
    elif windows_generated:
        require(
            type(data["windowsBuild"]) is int
            and 1 <= data["windowsBuild"] <= 65535,
            f"{path} must contain a Windows build from 1 through 65535",
        )
        for key in ("updateVersion", "releaseVersion"):
            require(
                isinstance(data[key], str) and SEMVER.fullmatch(data[key]),
                f"{path} must contain a stable {key}",
            )
    source = load_version(ROOT)
    require(
        data["version"] == (source.mac_version if platform == "macos" else source.windows_version),
        f"{path} version does not match release/version.json",
    )
    if platform == "macos":
        require(
            data["build"] == source.mac_build,
            f"{path} build does not match release/version.json",
        )
    elif windows_generated:
        require(data["windowsBuild"] == source.windows_build,
                f"{path} build does not match release/version.json")
        require(data["updateVersion"] == source.windows_update_version,
                f"{path} update version does not match release/version.json")
        require(data["releaseVersion"] == source.windows_release_version,
                f"{path} release version does not match release/version.json")
        require(data["msixVersion"] == source.msix_version,
                f"{path} MSIX version does not match release/version.json")
    return data


def project_values_for_bundle_identifier(
    source: str, bundle_identifier: str, setting: str
) -> set[str]:
    values: set[str] = set()
    matching_configurations = 0
    for body in re.findall(r"buildSettings = \{(.*?)\n\s*\};", source, re.DOTALL):
        settings = dict(re.findall(r"\b([A-Z][A-Z0-9_]*) = ([^;]+);", body))
        configured_bundle = settings.get("PRODUCT_BUNDLE_IDENTIFIER", "").strip('"')
        if configured_bundle != bundle_identifier:
            continue
        matching_configurations += 1
        if setting in settings:
            values.add(settings[setting].strip('"'))
    require(
        matching_configurations > 0,
        f"no build configurations found for {bundle_identifier}",
    )
    require(
        len(values) == 1,
        f"{setting} for {bundle_identifier} must have one value, found {sorted(values)}",
    )
    return values


def project_value_for_bundle_identifier(
    source: str, bundle_identifier: str, setting: str
) -> str:
    return next(iter(project_values_for_bundle_identifier(
        source, bundle_identifier, setting
    )))


def validate_macos() -> dict[str, str]:
    manifest = load_manifest("macos")
    version = str(manifest["version"])
    build = str(manifest["build"])
    project = read("macos/SIDEY.xcodeproj/project.pbxproj")
    bundle = "app.sidey.desktop.appstore"
    if (ROOT / "macos" / "Config" / "Version.xcconfig").is_file():
        check_platform("macos", ROOT)
        require("MARKETING_VERSION =" not in project,
                "Mac App Store project must not duplicate the Product Version")
        require("CURRENT_PROJECT_VERSION =" not in project,
                "Mac App Store project must not duplicate the macOS build number")
        require(project.count("/* Version.xcconfig */") >= 3,
                "Mac App Store Debug and Release must use Version.xcconfig")
    else:
        require(project_value_for_bundle_identifier(project, bundle, "MARKETING_VERSION") == version,
                "Mac App Store project version does not match release/macos.json")
        require(project_value_for_bundle_identifier(project, bundle, "CURRENT_PROJECT_VERSION") == build,
                "Mac App Store project build does not match release/macos.json")
    release_data = read("website/src/data/releases.ts")
    require("appStoreURLForLocale" in release_data,
            "website macOS installation must use the Mac App Store")
    require(".dmg" not in release_data,
            "website release data must not offer retired macOS installers")
    return {"version": version, "build": build, "tag": f"appstore-{version}-{build}"}


def windows_release_tag(version: str) -> str:
    tag = f"windows-v{version}"
    return tag


def validate_published_windows_release() -> dict[str, object]:
    path = "website/src/data/release-notes/recent.json"
    entries = json.loads(read(path))
    require(isinstance(entries, list) and bool(entries),
            f"{path} must contain published Windows releases")
    retained: list[dict[str, object]] = []
    versions: set[str] = set()
    for entry in entries:
        require(isinstance(entry, dict), f"{path} contains an invalid release")
        version = entry.get("version")
        require(isinstance(version, str) and SEMVER.fullmatch(version) is not None,
                f"{path} releases must contain a stable semantic version")
        require(version not in versions, f"{path} contains duplicate release {version}")
        versions.add(version)
        tag = windows_release_tag(version)
        require(entry.get("url") == f"https://github.com/sidey-app/SIDEY/releases/tag/{tag}",
                f"published Windows release {version} has the wrong tag URL")
        asset = entry.get("asset")
        if "asset" in entry:
            require(isinstance(asset, dict),
                    f"published Windows release {version} has invalid installer metadata")
            installer_name = f"SIDEY-Windows-x64-v{version}-Setup.exe"
            require(asset.get("url") == (
                f"https://github.com/sidey-app/SIDEY/releases/download/{tag}/{installer_name}"
            ), f"published Windows release {version} has the wrong installer URL")
            digest = asset.get("sha256")
            require(isinstance(digest, str) and re.fullmatch(r"[0-9a-f]{64}", digest) is not None,
                    f"published Windows release {version} must contain a SHA-256 digest")
        if int(version.split(".")[0]) >= 2:
            retained.append(entry)
    require(bool(retained), f"{path} must contain a published Windows release from version 2")
    latest = max(retained, key=lambda entry: tuple(map(int, str(entry["version"]).split("."))))
    require(isinstance(latest.get("asset"), dict),
            "latest published Windows release must contain installer metadata")
    return latest


def validate_windows(allow_unreleased_source: bool = False) -> dict[str, str]:
    manifest = load_manifest("windows")
    version = str(manifest["version"])
    generated_path = ROOT / "windows" / "Version.props"
    generated_versions = generated_path.is_file()
    update_version = str(manifest.get("updateVersion", version))
    release_version = str(manifest.get("releaseVersion", version))
    tag = windows_release_tag(release_version)
    installer_name = f"SIDEY-Windows-x64-v{release_version}-Setup.exe"
    installer_url = (
        f"https://github.com/sidey-app/SIDEY/releases/download/{tag}/{installer_name}"
    )
    notes = f"docs/releases/{tag}.md"

    project = ET.parse(ROOT / "windows" / "src" / "Sidey.App" / "Sidey.App.csproj")
    project_values = {
        element.tag: (element.text or "") for element in project.getroot().iter()
    }
    if generated_versions:
        check_platform("windows", ROOT)
        require(
            all(name not in project_values for name in
                ("Version", "FileVersion", "AssemblyVersion", "InformationalVersion")),
            "Windows app project must not duplicate generated version properties",
        )
        generated = ET.parse(generated_path)
        values = {element.tag: (element.text or "") for element in generated.getroot().iter()}
        source_version = values.get("SideyProductVersion", "")
    else:
        values = project_values
        source_version = values.get("Version", "")
    require(SEMVER.fullmatch(source_version) is not None,
            "Windows project must contain a stable semantic version")
    if source_version != version:
        require(allow_unreleased_source,
                "Windows project version does not match release/windows.json")
        require(
            tuple(map(int, source_version.split(".")))
            > tuple(map(int, version.split("."))),
                "unreleased Windows source version must be newer than the public release")
    if generated_versions:
        resolved = load_version(ROOT)
        require(values.get("FileVersion") == resolved.msix_version,
                "Windows file version does not match the MSIX-compatible version")
        require(values.get("SideyMsixVersion") == resolved.msix_version,
                "Windows MSIX version property does not match release/version.json")
        require(values.get("SideyWindowsUpdateVersion") == resolved.windows_update_version,
                "Windows update version property does not match release/version.json")
        require(values.get("SideyWindowsReleaseVersion") == resolved.windows_release_version,
                "Windows release version property does not match release/version.json")
        require(values.get("AssemblyVersion") == resolved.assembly_version,
                "Windows assembly version does not match the Product Version")
        require(values.get("InformationalVersion") == source_version,
                "Windows informational version does not match the Product Version")
    else:
        require(values.get("FileVersion") == f"{source_version}.0",
                "Windows file version does not match the project version")
        require(values.get("AssemblyVersion") == f"{source_version}.0",
                "Windows assembly version does not match the project version")
    update_source_paths = sorted(
        (ROOT / "windows" / "src" / "Sidey.Platform.Windows").rglob(
            "WindowsUpdateService.cs"
        )
    )
    require(
        len(update_source_paths) == 1,
        "Windows updater source must resolve to exactly one WindowsUpdateService.cs",
    )
    update_source = update_source_paths[0].read_text(encoding="utf-8")
    if generated_versions:
        require(re.search(r"\bconst\s+string\s+CurrentVersion\s*=", update_source) is None,
                "Windows updater must derive its current version from the app assembly")
    else:
        require(f'CurrentVersion = "{source_version}"' in update_source,
                "Windows updater version does not match the project version")
    if not allow_unreleased_source:
        require((ROOT / notes).is_file(), f"Windows release notes are missing: {notes}")

    release_data = read("website/src/data/releases.ts")
    validate_published_windows_release()
    require('getWebsiteReleases("windows")[0]' in release_data,
            "website release data must select the latest published Windows release")
    require("version: windowsRelease.version" in release_data,
            "website release data must derive the Windows version from the published release")
    require("url: windowsRelease.asset.url" in release_data,
            "website release data must use the published Windows installer URL")
    require("sha256: windowsRelease.asset.sha256" in release_data,
            "website release data must use the published Windows installer digest")

    return {
        "version": version,
        "product_version": version,
        "update_version": update_version,
        "release_version": release_version,
        "tag": tag,
        "installer_name": installer_name,
        "installer_url": installer_url,
        "release_notes": notes,
        "source_version": source_version,
    }


def write_github_output(path: Path, values: dict[str, str]) -> None:
    with path.open("a", encoding="utf-8", newline="\n") as output:
        for key, value in values.items():
            require("\n" not in value and "\r" not in value, f"invalid output: {key}")
            output.write(f"{key}={value}\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--platform", choices=("all", "macos", "windows"), default="all")
    parser.add_argument("--github-output", type=Path)
    parser.add_argument(
        "--allow-pending-appcast",
        action="store_true",
        help=argparse.SUPPRESS,
    )
    parser.add_argument(
        "--allow-unreleased-source",
        action="store_true",
        help="allow a Windows source version newer than the current public manifest",
    )
    args = parser.parse_args()

    outputs: dict[str, str] = {}
    try:
        require(
            not args.allow_pending_appcast,
            "the direct macOS release workflow is retired; use archive_app_store.sh",
        )
        if args.platform in ("all", "macos"):
            outputs = validate_macos()
        if args.platform in ("all", "windows"):
            windows = validate_windows(args.allow_unreleased_source)
            outputs = windows if args.platform == "windows" else outputs
    except (ConsistencyError, VersionError, ET.ParseError, json.JSONDecodeError) as error:
        print(f"release consistency error: {error}", file=sys.stderr)
        return 1

    if args.github_output:
        write_github_output(args.github_output, outputs)
    if args.platform == "all":
        print("release metadata is consistent for macOS and Windows")
    else:
        print(f"release metadata is consistent for {args.platform}: {outputs['tag']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
