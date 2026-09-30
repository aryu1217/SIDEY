#!/usr/bin/env python3
"""Resolve and validate SIDEY HeadVer versions and platform mirrors."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import date, datetime, timezone
import json
import re
import subprocess
import sys
from pathlib import Path
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
MAX_MSIX_COMPONENT = 65535
HEADVER_WEEK = re.compile(r"^[0-9]{4}$")
STABLE_VERSION = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")


class VersionError(RuntimeError):
    """Version metadata is malformed or inconsistent."""


@dataclass(frozen=True)
class SideyVersion:
    head: int
    yearweek: str
    windows_build: int
    mac_build: int

    @property
    def windows_version(self) -> str:
        return f"{self.head}.{self.yearweek}.{self.windows_build}"

    @property
    def mac_version(self) -> str:
        return f"{self.head}.{self.yearweek}.{self.mac_build}"

    @property
    def product_version(self) -> str:
        """Windows compatibility name for existing release callers."""
        return self.windows_version

    @property
    def windows_update_version(self) -> str:
        return self.windows_version

    @property
    def windows_release_version(self) -> str:
        return self.windows_version

    @property
    def msix_version(self) -> str:
        return f"{self.windows_version}.0"

    @property
    def assembly_version(self) -> str:
        return self.msix_version

    def as_dict(self) -> dict[str, object]:
        return {
            "head": self.head,
            "yearweek": self.yearweek,
            "windowsBuild": self.windows_build,
            "macBuild": self.mac_build,
            "windowsVersion": self.windows_version,
            "macVersion": self.mac_version,
            "productVersion": self.product_version,
            "windowsUpdateVersion": self.windows_update_version,
            "windowsReleaseVersion": self.windows_release_version,
            "msixVersion": self.msix_version,
            "assemblyVersion": self.assembly_version,
        }


@dataclass(frozen=True)
class LegacyVersion:
    product_version: str
    mac_build: int


def require(condition: bool, message: str) -> None:
    if not condition:
        raise VersionError(message)


def _integer(data: dict[str, object], key: str) -> int:
    value = data.get(key)
    require(type(value) is int, f"{key} must be an integer")
    return value


def parse_version_data(data: dict[str, object]) -> SideyVersion:
    expected = {"schema", "head", "yearweek", "windowsBuild", "macBuild"}
    require(set(data) == expected, f"version source must contain exactly {sorted(expected)}")
    require(data["schema"] == 2, "version source has an unsupported schema")
    head = _integer(data, "head")
    windows_build = _integer(data, "windowsBuild")
    mac_build = _integer(data, "macBuild")
    yearweek = data["yearweek"]
    require(1 <= head <= MAX_MSIX_COMPONENT, "head must be from 1 through 65535")
    require(type(yearweek) is str and HEADVER_WEEK.fullmatch(yearweek) is not None,
            "yearweek must be four ISO week-year digits")
    try:
        date.fromisocalendar(2000 + int(yearweek[:2]), int(yearweek[2:]), 1)
    except ValueError as error:
        raise VersionError(f"yearweek is not an ISO week: {yearweek}") from error
    require(1 <= windows_build <= MAX_MSIX_COMPONENT,
            "windowsBuild must be from 1 through 65535")
    require(mac_build >= 1, "macBuild must be a positive integer")
    return SideyVersion(head, yearweek, windows_build, mac_build)


def load_version(root: Path = ROOT) -> SideyVersion:
    path = root / "release" / "version.json"
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise VersionError(f"cannot read {path}: {error}") from error
    require(isinstance(data, dict), f"{path} must contain a JSON object")
    return parse_version_data(data)


def load_version_at_git_ref(reference: str, root: Path = ROOT) -> SideyVersion | LegacyVersion:
    def show(path: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(["git", "show", f"{reference}:{path}"], cwd=root,
                              check=False, capture_output=True, text=True)

    completed = show("release/version.json")
    if completed.returncode != 0:
        windows = show("release/windows.json")
        macos = show("release/macos.json")
        require(windows.returncode == 0 and macos.returncode == 0,
                f"cannot read version metadata at {reference}")
        try:
            windows_data = json.loads(windows.stdout)
            macos_data = json.loads(macos.stdout)
        except json.JSONDecodeError as error:
            raise VersionError(f"legacy release metadata at {reference} is invalid JSON: {error}") from error
        return LegacyVersion(windows_data["version"], macos_data["build"])
    try:
        data = json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        raise VersionError(f"release/version.json at {reference} is invalid JSON: {error}") from error
    require(isinstance(data, dict), f"release/version.json at {reference} must be an object")
    if data.get("schema") == 1:
        require(STABLE_VERSION.fullmatch(str(data.get("productVersion", ""))) is not None,
                "legacy Product Version is invalid")
        return LegacyVersion(data["productVersion"], data["macBuild"])
    return parse_version_data(data)


def validate_transition(previous: SideyVersion | LegacyVersion, current: SideyVersion) -> None:
    if isinstance(previous, LegacyVersion):
        previous_windows = tuple(map(int, previous.product_version.split(".")))
        require(tuple(map(int, current.windows_version.split("."))) > previous_windows,
                "Windows HeadVer must increase from the legacy Product Version")
    else:
        require((current.head, current.yearweek) >= (previous.head, previous.yearweek),
                "head/yearweek must not decrease")
        require(current.windows_build >= previous.windows_build,
                "windowsBuild must not decrease")
        require(tuple(map(int, current.windows_version.split("."))) >=
                tuple(map(int, previous.windows_version.split("."))),
                "Windows HeadVer must not decrease")
    require(current.mac_build >= previous.mac_build, "macBuild must not decrease")


def render_windows_props(version: SideyVersion) -> str:
    return f"""<Project>
  <!-- Generated by scripts/sidey_version.py from release/version.json. -->
  <PropertyGroup>
    <SideyProductVersion>{version.windows_version}</SideyProductVersion>
    <SideyWindowsBuild>{version.windows_build}</SideyWindowsBuild>
    <SideyWindowsUpdateVersion>{version.windows_version}</SideyWindowsUpdateVersion>
    <SideyWindowsReleaseVersion>{version.windows_version}</SideyWindowsReleaseVersion>
    <SideyMsixVersion>{version.msix_version}</SideyMsixVersion>
    <Version>{version.windows_version}</Version>
    <VersionPrefix>{version.windows_version}</VersionPrefix>
    <AssemblyVersion>{version.assembly_version}</AssemblyVersion>
    <FileVersion>{version.msix_version}</FileVersion>
    <InformationalVersion>{version.windows_version}</InformationalVersion>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
  </PropertyGroup>
</Project>
"""


def render_macos_xcconfig(version: SideyVersion) -> str:
    return ("// Generated by scripts/sidey_version.py from release/version.json.\n"
            f"MARKETING_VERSION = {version.mac_version}\n"
            f"CURRENT_PROJECT_VERSION = {version.mac_build}\n")


def render_release_manifest(platform: str, version: SideyVersion) -> str:
    if platform == "windows":
        data = {"schema": 1, "platform": "windows", "channel": "production",
                "version": version.windows_version, "windowsBuild": version.windows_build,
                "updateVersion": version.windows_version,
                "releaseVersion": version.windows_version,
                "msixVersion": version.msix_version}
    elif platform == "macos":
        data = {"schema": 1, "platform": "macos", "channel": "appstore",
                "version": version.mac_version, "build": version.mac_build}
    else:
        raise VersionError(f"unsupported platform: {platform}")
    return json.dumps(data, indent=2, ensure_ascii=False) + "\n"


def _expected_files(platform: str, version: SideyVersion) -> dict[Path, str]:
    if platform == "windows":
        return {Path("release/windows.json"): render_release_manifest("windows", version),
                Path("windows/Version.props"): render_windows_props(version)}
    if platform == "macos":
        return {Path("release/macos.json"): render_release_manifest("macos", version),
                Path("macos/Config/Version.xcconfig"): render_macos_xcconfig(version)}
    raise VersionError(f"unsupported platform: {platform}")


def check_platform(platform: str, root: Path = ROOT) -> None:
    version = load_version(root)
    for relative_path, expected in _expected_files(platform, version).items():
        path = root / relative_path
        require(path.is_file(), f"generated version mirror is missing: {relative_path}")
        require(path.read_text(encoding="utf-8") == expected,
                f"generated version mirror is stale: {relative_path}; run "
                f"python3 scripts/sidey_version.py --write {platform}")
    if platform == "windows":
        try:
            ET.parse(root / "windows" / "Version.props")
        except ET.ParseError as error:
            raise VersionError(f"windows/Version.props is invalid XML: {error}") from error


def write_platform(platform: str, root: Path = ROOT) -> None:
    version = load_version(root)
    for relative_path, content in _expected_files(platform, version).items():
        path = root / relative_path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8", newline="\n")


def advance_yearweek(root: Path = ROOT, current_date: date | None = None) -> str:
    """Advance the shared ISO week once and refresh both platform mirrors."""
    branch = subprocess.run(["git", "symbolic-ref", "--quiet", "--short", "HEAD"],
                            cwd=root, capture_output=True, text=True, check=False)
    require(branch.returncode == 0 and branch.stdout.strip().startswith("shared/"),
            "yearweek may only advance on a shared/* task branch")
    previous = load_version(root)
    today = current_date or datetime.now(timezone.utc).date()
    iso_year, iso_week, _ = today.isocalendar()
    require(2000 <= iso_year <= 2099, "ISO week-year is outside HeadVer's 2000–2099 range")
    candidate = f"{iso_year % 100:02}{iso_week:02}"
    require(candidate >= previous.yearweek, "new yearweek must not precede the source")
    if candidate == previous.yearweek:
        return candidate
    source = root / "release/version.json"
    data = json.loads(source.read_text(encoding="utf-8"))
    data["yearweek"] = candidate
    updated = parse_version_data(data)
    localization = root / "release/app-store-localizations.json"
    content = localization.read_text(encoding="utf-8")
    old = f'"version": "{previous.mac_version}"'
    require(content.count(old) == 1, "App Store localization release version is stale")
    source.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8", newline="\n")
    localization.write_text(content.replace(old, f'"version": "{updated.mac_version}"'),
                            encoding="utf-8", newline="\n")
    write_platform("macos", root)
    write_platform("windows", root)
    return candidate


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--check", choices=("windows", "macos"))
    action.add_argument("--write", choices=("windows", "macos"))
    action.add_argument("--json", action="store_true")
    action.add_argument("--advance-yearweek", action="store_true")
    action.add_argument("--check-transition", metavar="GIT_REF")
    action.add_argument("--get", choices=("head", "yearweek", "windowsBuild", "macBuild",
                                          "windowsVersion", "macVersion", "productVersion",
                                          "windowsUpdateVersion", "windowsReleaseVersion",
                                          "msixVersion", "assemblyVersion"))
    args = parser.parse_args()
    try:
        if args.check:
            check_platform(args.check)
            print(f"{args.check} version metadata is current")
        elif args.write:
            write_platform(args.write)
            print(f"updated {args.write} version mirrors")
        elif args.advance_yearweek:
            print(f"shared ISO yearweek is {advance_yearweek()}")
        elif args.check_transition:
            validate_transition(load_version_at_git_ref(args.check_transition), load_version())
            print(f"version transition from {args.check_transition} is valid")
        else:
            data = load_version().as_dict()
            if args.json:
                print(json.dumps(data, separators=(",", ":")))
            else:
                print(data[args.get])
        return 0
    except VersionError as error:
        print(f"version error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
