"""Git queries and branch ownership policy for repository tools."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

from .errors import WorkflowError
from .process import run


WINDOWS_UI_LOCALIZATION_MIRRORS = {
    f"windows/src/Sidey.App/Langs/{locale}.json"
    for locale in (
        "bg-BG", "cs-CZ", "de-DE", "en-US", "es-ES", "fr-FR", "he-IL",
        "it-IT", "ja-JP", "ko-KR", "nl-BE", "nl-NL", "pl-PL", "pt-BR",
        "pt-PT", "ro-RO", "ru-RU", "sr-Cyrl-RS", "sr-Latn-RS", "tr-TR",
        "uk-UA", "zh-CN", "zh-TW",
    )
}
WINDOWS_INTERNAL_LOCALIZATION_MIRRORS = {
    f"windows/src/Sidey.App/InternalLangs/{locale}.json"
    for locale in (
        "bg-BG", "cs-CZ", "de-DE", "en-US", "es-ES", "fr-FR", "he-IL",
        "it-IT", "ja-JP", "ko-KR", "nl-BE", "nl-NL", "pl-PL", "pt-BR",
        "pt-PT", "ro-RO", "ru-RU", "sr-Cyrl-RS", "sr-Latn-RS", "tr-TR",
        "uk-UA", "zh-CN", "zh-TW",
    )
}
MACOS_LOCALIZATION_MIRRORS = {
    "macos/SIDEY/Resources/Localizable.xcstrings",
    "macos/SIDEY/Resources/InternalLocalizable.xcstrings",
    "macos/SIDEY/Resources/Commerce/commerce-localizations.json",
    "macos/SIDEYAppStore.storekit",
}
# Locale-source changes must keep the one macOS source adapter and all checked-in
# platform mirrors atomic.
LOCALIZATION_PLATFORM_PATHS = (
    WINDOWS_UI_LOCALIZATION_MIRRORS
    | WINDOWS_INTERNAL_LOCALIZATION_MIRRORS
    | MACOS_LOCALIZATION_MIRRORS
    | {"scripts/macos/sync_commerce_localizations.py"}
)
PRODUCT_VERSION_MIRRORS = {
    "release/macos.json",
    "macos/Config/Version.xcconfig",
    "release/windows.json",
    "windows/Version.props",
    "release/app-store-localizations.json",
}
STABLE_VERSION = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")


def validate_product_version_transition(
    root: str | Path | None,
    base: str | None,
    revision: str,
) -> None:
    """Allow one atomic shared version bump with only its generated mirrors."""

    if root is None or base is None:
        raise WorkflowError(
            "Product Version transition requires an explicit root and base"
        )
    try:
        before = json.loads(git(root, "show", f"{base}:release/version.json"))
        after = json.loads(git(root, "show", f"{revision}:release/version.json"))
    except (json.JSONDecodeError, WorkflowError) as error:
        raise WorkflowError(
            f"Cannot compare Product Version against the base: {error}"
        ) from error

    if (isinstance(before, dict) and isinstance(after, dict)
            and before.get("schema") == after.get("schema") == 2):
        keys = {"schema", "head", "yearweek", "windowsBuild", "macBuild"}
        if (set(before) != keys or set(after) != keys
                or type(before["head"]) is not int or type(after["head"]) is not int
                or type(before["yearweek"]) is not str
                or type(after["yearweek"]) is not str
                or (after["head"], after["yearweek"])
                <= (before["head"], before["yearweek"])
                or after["windowsBuild"] != before["windowsBuild"]
                or after["macBuild"] != before["macBuild"]):
            raise WorkflowError("Shared HeadVer head/yearweek transition must preserve builds")
        localization_path = "release/app-store-localizations.json"
        localization_before = json.loads(git(root, "show", f"{base}:{localization_path}"))
        localization_after = json.loads(git(root, "show", f"{revision}:{localization_path}"))
        expected_before = f"{before['head']}.{before['yearweek']}.{before['macBuild']}"
        expected_after = f"{after['head']}.{after['yearweek']}.{after['macBuild']}"
        if (localization_before.get("release") != {
                "version": expected_before, "build": before["macBuild"]}
                or localization_after.get("release") != {
                    "version": expected_after, "build": after["macBuild"]}
                or {k: v for k, v in localization_before.items() if k != "release"}
                != {k: v for k, v in localization_after.items() if k != "release"}):
            raise WorkflowError("App Store localization manifest may change only its matching release version")
        for platform in ("macos", "windows"):
            run(root, sys.executable, "scripts/sidey_version.py", "--check", platform)
        return

    keys = {"schema", "productVersion", "windowsRevision", "macBuild"}
    if (
        not isinstance(before, dict)
        or not isinstance(after, dict)
        or set(before) != keys
        or set(after) != keys
        or type(before["schema"]) is not int
        or type(after["schema"]) is not int
        or before["schema"] != 1
        or after["schema"] != 1
        or type(before["productVersion"]) is not str
        or type(after["productVersion"]) is not str
        or STABLE_VERSION.fullmatch(before["productVersion"]) is None
        or STABLE_VERSION.fullmatch(after["productVersion"]) is None
        or type(before["windowsRevision"]) is not int
        or type(after["windowsRevision"]) is not int
        or not 0 <= before["windowsRevision"] <= 999
        or after["windowsRevision"] != 0
        or type(before["macBuild"]) is not int
        or type(after["macBuild"]) is not int
        or before["macBuild"] < 1
        or after["macBuild"] != before["macBuild"]
    ):
        raise WorkflowError(
            "Shared Product Version transition must preserve schema and macBuild, "
            "reset windowsRevision to 0, and use stable versions"
        )

    previous_version = tuple(map(int, before["productVersion"].split(".")))
    next_version = tuple(map(int, after["productVersion"].split(".")))
    if next_version <= previous_version:
        raise WorkflowError("Shared Product Version must increase")

    localization_path = "release/app-store-localizations.json"
    try:
        localization_before = json.loads(git(root, "show", f"{base}:{localization_path}"))
        localization_after = json.loads(git(root, "show", f"{revision}:{localization_path}"))
    except (json.JSONDecodeError, WorkflowError) as error:
        raise WorkflowError(
            f"Cannot compare App Store localization release to the base: {error}"
        ) from error
    if (
        not isinstance(localization_before, dict)
        or not isinstance(localization_after, dict)
        or not isinstance(localization_before.get("release"), dict)
        or not isinstance(localization_after.get("release"), dict)
        or localization_before["release"] != {
            "version": before["productVersion"], "build": before["macBuild"]
        }
        or localization_after["release"] != {
            "version": after["productVersion"], "build": after["macBuild"]
        }
        or {k: v for k, v in localization_before.items() if k != "release"}
        != {k: v for k, v in localization_after.items() if k != "release"}
    ):
        raise WorkflowError(
            "App Store localization manifest may change only its matching release version"
        )

    for platform in ("macos", "windows"):
        run(root, sys.executable, "scripts/sidey_version.py", "--check", platform)


def git(root: str | Path, *args: str) -> str:
    """Run Git from *root* and return its standard output."""

    return run(root, "git", *args)


def root_at(path: str | Path) -> Path:
    """Return the repository root containing *path*."""

    return Path(git(path, "rev-parse", "--show-toplevel")).resolve()


def branch(root: str | Path) -> str:
    """Return the current branch name for *root*."""

    return git(root, "symbolic-ref", "--quiet", "--short", "HEAD")


def changed_paths(
    root: str | Path,
    base: str,
    revision: str = "HEAD",
) -> list[str]:
    """Return paths changed since the merge base of two revisions."""

    merge_base = git(root, "merge-base", base, revision)
    output = git(
        root,
        "diff",
        "--no-renames",
        "--name-only",
        "-z",
        merge_base,
        revision,
    )
    return sorted(set(output.split("\0")) - {""})


def platform_for(path: str) -> str:
    """Return the branch platform that owns *path*."""

    if path in {"release/macos.json", "release/app-store-localizations.json"}:
        return "macos"
    if path == "release/windows.json":
        return "windows"
    if path.startswith(("macos/", "scripts/macos/")):
        return "macos"
    if path in {
        ".github/workflows/macos-build-and-tests.yml",
        ".github/workflows/validate-macos.yml",
    }:
        return "macos"
    if path.startswith(("windows/", "scripts/windows/")):
        return "windows"
    if re.fullmatch(
        r"\.github/workflows/"
        r"(?:publish-windows-release|validate-windows|"
        r"windows-build-and-tests|windows-release)\.yml",
        path,
    ):
        return "windows"
    return "shared"


def validate_paths(
    branch_name: str,
    paths: list[str],
    *,
    root: str | Path | None = None,
    base: str | None = None,
    revision: str = "HEAD",
) -> str:
    """Validate path ownership and generated-mirror exceptions."""

    match = re.fullmatch(
        r"(shared|macos|windows)/[A-Za-z0-9][A-Za-z0-9._/-]*",
        branch_name,
    )
    if not match:
        raise WorkflowError(
            "Implementation requires shared/*, macos/* or windows/* "
            "(never main)"
        )

    platform = match[1]
    mac_build_change = platform == "macos" and "release/version.json" in paths
    windows_build_change = platform == "windows" and "release/version.json" in paths
    product_version_change = (
        platform == "shared"
        and "release/version.json" in paths
        and PRODUCT_VERSION_MIRRORS.issubset(paths)
    )
    localization_source_change = (
        platform == "shared"
        and any(
            path == "assets/v1/ui-localizations.json"
            or path.startswith(
                (
                    "assets/v1/locale/client/",
                    "assets/v1/locale/commerce/",
                    "assets/v1/locale/internal/",
                )
            )
            for path in paths
        )
    )
    invalid = [
        path for path in paths
        if platform_for(path) != platform
        and not (mac_build_change and path == "release/version.json")
        and not (windows_build_change and path == "release/version.json")
        and not (product_version_change and path in PRODUCT_VERSION_MIRRORS)
        and not (
            localization_source_change
            and path in LOCALIZATION_PLATFORM_PATHS
        )
    ]
    if invalid:
        changed = ", ".join(invalid)
        raise WorkflowError(
            f"{branch_name} crosses its platform boundary: {changed}"
        )
    if product_version_change:
        validate_product_version_transition(root, base, revision)
    if mac_build_change:
        mirrors = {"release/macos.json", "macos/Config/Version.xcconfig"}
        if not mirrors.issubset(paths):
            raise WorkflowError(
                "macOS build counter change must include both generated macOS mirrors"
            )
        if root is None or base is None:
            raise WorkflowError(
                "macOS build counter ownership requires an explicit root and base"
            )
        try:
            before = json.loads(git(root, "show", f"{base}:release/version.json"))
            after = json.loads(git(root, "show", f"{revision}:release/version.json"))
        except (json.JSONDecodeError, WorkflowError) as error:
            raise WorkflowError(
                f"Cannot compare macOS build counter to the base: {error}"
            ) from error
        if (
            not isinstance(before, dict)
            or not isinstance(after, dict)
            or set(before) not in (
                {"schema", "productVersion", "windowsRevision", "macBuild"},
                {"schema", "head", "yearweek", "windowsBuild", "macBuild"},
            )
            or set(after) != set(before)
            or any(
                type(after[key]) is not type(before[key])
                or after[key] != before[key]
                for key in before if key != "macBuild"
            )
            or type(after["macBuild"]) is not int
            or type(before["macBuild"]) is not int
            or after["macBuild"] <= before["macBuild"]
        ):
            raise WorkflowError(
                "macOS branch may only increase macBuild in release/version.json"
            )
        if after["schema"] == 2:
            localization_path = "release/app-store-localizations.json"
            if localization_path not in paths:
                raise WorkflowError("HeadVer macOS build must update App Store localization release")
            localization_before = json.loads(git(root, "show", f"{base}:{localization_path}"))
            localization_after = json.loads(git(root, "show", f"{revision}:{localization_path}"))
            expected_before = f"{before['head']}.{before['yearweek']}.{before['macBuild']}"
            expected_after = f"{after['head']}.{after['yearweek']}.{after['macBuild']}"
            if (localization_before.get("release") != {
                    "version": expected_before, "build": before["macBuild"]}
                    or localization_after.get("release") != {
                        "version": expected_after, "build": after["macBuild"]}
                    or {k: v for k, v in localization_before.items() if k != "release"}
                    != {k: v for k, v in localization_after.items() if k != "release"}):
                raise WorkflowError("App Store localization release must match HeadVer macOS build")
            run(root, sys.executable, "scripts/sidey_version.py", "--check", "macos")
    if windows_build_change:
        mirrors = {"release/windows.json", "windows/Version.props"}
        if not mirrors.issubset(paths):
            raise WorkflowError("Windows build counter change must include both Windows mirrors")
        if root is None or base is None:
            raise WorkflowError("Windows build counter requires an explicit root and base")
        before = json.loads(git(root, "show", f"{base}:release/version.json"))
        after = json.loads(git(root, "show", f"{revision}:release/version.json"))
        keys = {"schema", "head", "yearweek", "windowsBuild", "macBuild"}
        if (set(before) != keys or set(after) != keys
                or before["schema"] != 2 or after["schema"] != 2
                or any(after[key] != before[key] for key in keys - {"windowsBuild"})
                or type(after["windowsBuild"]) is not int
                or after["windowsBuild"] <= before["windowsBuild"]):
            raise WorkflowError("Windows branch may only increase windowsBuild")
        run(root, sys.executable, "scripts/sidey_version.py", "--check", "windows")
    return platform
