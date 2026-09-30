import json
import subprocess
import sys
import unittest
from pathlib import Path
from unittest.mock import call, patch

sys.path.insert(0, str(Path(__file__).parents[1] / "skills"))

from sidey_tools import WorkflowError  # noqa: E402
from sidey_tools.process import decode_output, run  # noqa: E402
from sidey_tools.repository import (  # noqa: E402
    changed_paths,
    platform_for,
    validate_paths,
)
from validate_change import (  # noqa: E402
    commit_messages,
    resolve_change,
    validate_pr_paths,
    verify_commit_contract,
    verify_gate,
    verify_pr_contract,
)


class GateTests(unittest.TestCase):
    def test_process_output_normalizes_line_endings(self):
        self.assertEqual(decode_output(b"a\r\nb\rc\n"), "a\nb\nc\n")

    def test_process_failure_uses_workflow_error(self):
        completed = subprocess.CompletedProcess(
            args=("git", "status"),
            returncode=2,
            stdout=b"",
            stderr=b"failure\r\n",
        )
        with patch(
            "sidey_tools.process.subprocess.run",
            return_value=completed,
        ):
            with self.assertRaisesRegex(
                WorkflowError,
                "git status failed: failure",
            ):
                run(Path("."), "git", "status")

    def test_validate_change_uses_repository_git_helper(self):
        self.assertEqual(
            commit_messages.__globals__["git"].__module__,
            "sidey_tools.repository",
        )

    def test_changed_paths_uses_merge_base_and_nul_delimited_diff(self):
        with patch(
            "sidey_tools.repository.git",
            side_effect=["merge-base", "b.py\x00a.py\x00b.py\x00"],
        ) as git:
            self.assertEqual(
                changed_paths(Path("."), "base", "head"),
                ["a.py", "b.py"],
            )
        self.assertEqual(
            git.call_args_list,
            [
                call(
                    Path("."),
                    "merge-base",
                    "base",
                    "head",
                ),
                call(
                    Path("."),
                    "diff",
                    "--no-renames",
                    "--name-only",
                    "-z",
                    "merge-base",
                    "head",
                ),
            ],
        )

    def test_path_validation_preserves_platform_policy(self):
        self.assertEqual(
            validate_paths("windows/task", ["windows/SIDEY/App.xaml.cs"]),
            "windows",
        )
        with self.assertRaisesRegex(WorkflowError, "platform boundary"):
            validate_paths(
                "windows/task",
                [".github/workflows/macos-build-and-tests.yml"],
            )

    def test_shared_localization_source_owns_only_required_platform_paths(self):
        sources = [
            "assets/v1/locale/client/ko.json",
            "assets/v1/locale/commerce/ko.json",
            "assets/v1/locale/internal/ko.json",
        ]
        mirrors = [
            "windows/src/Sidey.App/Langs/ko-KR.json",
            "windows/src/Sidey.App/InternalLangs/ko-KR.json",
            "macos/SIDEY/Resources/Localizable.xcstrings",
            "macos/SIDEY/Resources/InternalLocalizable.xcstrings",
            "macos/SIDEY/Resources/Commerce/commerce-localizations.json",
            "macos/SIDEYAppStore.storekit",
        ]
        adapter = "scripts/macos/sync_commerce_localizations.py"
        self.assertEqual(
            validate_paths(
                "shared/localization",
                [*sources, *mirrors, adapter],
            ),
            "shared",
        )
        for paths in (
            [mirrors[0]],
            [mirrors[1]],
            [sources[0], "windows/src/Sidey.App/MainWindow.xaml"],
        ):
            with self.subTest(paths=paths):
                with self.assertRaisesRegex(WorkflowError, "platform boundary"):
                    validate_paths("shared/localization", paths)

    def test_renamed_platform_workflows_keep_their_ownership(self):
        expected_platforms = {
            "release/macos.json": "macos",
            "release/windows.json": "windows",
            ".github/workflows/macos-build-and-tests.yml": "macos",
            ".github/workflows/validate-macos.yml": "macos",
            ".github/workflows/windows-build-and-tests.yml": "windows",
            ".github/workflows/validate-windows.yml": "windows",
            ".github/workflows/windows-release.yml": "windows",
            ".github/workflows/publish-windows-release.yml": "windows",
        }
        for path, expected in expected_platforms.items():
            with self.subTest(path=path):
                self.assertEqual(platform_for(path), expected)

    def test_app_store_metadata_stays_with_macos_release(self):
        paths = [
            "macos/SIDEY.xcodeproj/project.pbxproj",
            "release/macos.json",
            "release/app-store-localizations.json",
        ]
        self.assertEqual(validate_paths("macos/release", paths), "macos")
        for platform in ("windows", "shared"):
            with self.subTest(platform=platform):
                with self.assertRaisesRegex(WorkflowError, "platform boundary"):
                    validate_paths(
                        f"{platform}/release",
                        ["release/app-store-localizations.json"],
                    )

    def test_shared_product_version_transition_requires_atomic_generated_mirrors(self):
        paths = [
            "release/version.json",
            "release/macos.json",
            "macos/Config/Version.xcconfig",
            "release/windows.json",
            "windows/Version.props",
            "release/app-store-localizations.json",
            "docs/releases/windows-v2.1.0.md",
        ]
        before = {
            "schema": 1,
            "productVersion": "2.0.0",
            "windowsRevision": 4,
            "macBuild": 37,
        }
        after = {**before, "productVersion": "2.1.0", "windowsRevision": 0}
        localization_before = {"release": {"version": "2.0.0", "build": 37}, "app": {"bundle_id": "sidey"}}
        localization_after = {"release": {"version": "2.1.0", "build": 37}, "app": {"bundle_id": "sidey"}}
        root = Path(".")

        def check(candidate):
            with (
                patch(
                    "sidey_tools.repository.git",
                    side_effect=[json.dumps(before), json.dumps(candidate), json.dumps(localization_before), json.dumps(localization_after)],
                ) as git,
                patch("sidey_tools.repository.run") as run,
            ):
                result = validate_paths(
                    "shared/product-version", paths,
                    root=root, base="base", revision="head",
                )
            self.assertEqual(
                git.call_args_list,
                [
                    call(root, "show", "base:release/version.json"),
                    call(root, "show", "head:release/version.json"),
                    call(root, "show", "base:release/app-store-localizations.json"),
                    call(root, "show", "head:release/app-store-localizations.json"),
                ],
            )
            self.assertEqual(
                run.call_args_list,
                [
                    call(root, sys.executable, "scripts/sidey_version.py", "--check", "macos"),
                    call(root, sys.executable, "scripts/sidey_version.py", "--check", "windows"),
                ],
            )
            return result

        self.assertEqual(check(after), "shared")
        for candidate in (
            before,
            {**after, "productVersion": "1.9.9"},
            {**after, "productVersion": "2.01.0"},
            {**after, "windowsRevision": 1},
            {**after, "macBuild": 38},
            {**after, "schema": 2},
            {**after, "schema": True},
            {**after, "other": 1},
        ):
            with self.subTest(candidate=candidate):
                with (
                    patch(
                        "sidey_tools.repository.git",
                        side_effect=[json.dumps(before), json.dumps(candidate)],
                    ),
                    patch("sidey_tools.repository.run") as run,
                    self.assertRaisesRegex(WorkflowError, "Product Version"),
                ):
                    validate_paths(
                        "shared/product-version", paths,
                        root=root, base="base", revision="head",
                    )
                run.assert_not_called()

        for removed in paths[1:6]:
            with self.subTest(removed=removed):
                with self.assertRaisesRegex(WorkflowError, "platform boundary"):
                    validate_paths(
                        "shared/product-version",
                        [path for path in paths if path != removed],
                        root=root, base="base", revision="head",
                    )
        with self.assertRaisesRegex(WorkflowError, "platform boundary"):
            validate_paths(
                "shared/product-version", [*paths, "windows/src/Sidey.App/App.xaml.cs"],
                root=root, base="base", revision="head",
            )
        with self.assertRaisesRegex(WorkflowError, "explicit root and base"):
            validate_paths("shared/product-version", paths)
        with self.assertRaisesRegex(WorkflowError, "platform boundary"):
            validate_paths(
                "windows/product-version", paths,
                root=root, base="base", revision="head",
            )

        with (
            patch(
                "sidey_tools.repository.git",
                side_effect=[
                    json.dumps(before), json.dumps(after),
                    json.dumps(localization_before),
                    json.dumps({**localization_after, "app": {"bundle_id": "other"}}),
                ],
            ),
            patch("sidey_tools.repository.run") as run,
            self.assertRaisesRegex(WorkflowError, "App Store localization manifest"),
        ):
            validate_paths(
                "shared/product-version", paths,
                root=root, base="base", revision="head",
            )
        run.assert_not_called()

    def test_shared_product_version_transition_rejects_stale_mirror(self):
        paths = [
            "release/version.json",
            "release/macos.json",
            "macos/Config/Version.xcconfig",
            "release/windows.json",
            "windows/Version.props",
            "release/app-store-localizations.json",
        ]
        before = {
            "schema": 1, "productVersion": "2.0.0",
            "windowsRevision": 0, "macBuild": 37,
        }
        after = {**before, "productVersion": "2.1.0"}
        localization_before = {"release": {"version": "2.0.0", "build": 37}}
        localization_after = {"release": {"version": "2.1.0", "build": 37}}
        with (
            patch(
                "sidey_tools.repository.git",
                side_effect=[
                    json.dumps(before), json.dumps(after),
                    json.dumps(localization_before), json.dumps(localization_after),
                ],
            ),
            patch(
                "sidey_tools.repository.run",
                side_effect=["macOS mirror current", WorkflowError("Windows mirror stale")],
            ) as run,
            self.assertRaisesRegex(WorkflowError, "Windows mirror stale"),
        ):
            validate_paths(
                "shared/product-version", paths,
                root=Path("."), base="base", revision="head",
            )
        self.assertEqual(run.call_count, 2)

    def test_windows_headver_build_change_stays_on_windows_branch(self):
        paths = ["release/version.json", "release/windows.json",
                 "windows/Version.props"]
        before = {"schema": 2, "head": 2, "yearweek": "2640",
                  "windowsBuild": 7, "macBuild": 37}
        after = {**before, "windowsBuild": 8}
        with (patch("sidey_tools.repository.git", side_effect=[
                json.dumps(before), json.dumps(after)]),
              patch("sidey_tools.repository.run") as run):
            self.assertEqual(validate_paths("windows/headver-build", paths,
                                            root=Path("."), base="base", revision="head"),
                             "windows")
        run.assert_called_once_with(Path("."), sys.executable,
                                    "scripts/sidey_version.py", "--check", "windows")
        with (patch("sidey_tools.repository.git", side_effect=[
                json.dumps(before), json.dumps({**after, "macBuild": 38})]),
              self.assertRaisesRegex(WorkflowError, "only increase windowsBuild")):
            validate_paths("windows/headver-build", paths,
                           root=Path("."), base="base", revision="head")

    def test_macos_headver_build_change_updates_localization_release(self):
        paths = ["release/version.json", "release/macos.json",
                 "macos/Config/Version.xcconfig",
                 "release/app-store-localizations.json"]
        before = {"schema": 2, "head": 2, "yearweek": "2640",
                  "windowsBuild": 8, "macBuild": 37}
        after = {**before, "macBuild": 38}
        local_before = {"release": {"version": "2.2640.37", "build": 37}}
        local_after = {"release": {"version": "2.2640.38", "build": 38}}
        with (patch("sidey_tools.repository.git", side_effect=[
                json.dumps(before), json.dumps(after), json.dumps(local_before),
                json.dumps(local_after)]),
              patch("sidey_tools.repository.run") as run):
            self.assertEqual(validate_paths("macos/headver-build", paths,
                                            root=Path("."), base="base", revision="head"),
                             "macos")
        run.assert_called_once_with(Path("."), sys.executable,
                                    "scripts/sidey_version.py", "--check", "macos")

    def test_macos_build_counter_requires_both_mirrors_and_exact_base_diff(self):
        paths = [
            "release/version.json",
            "release/macos.json",
            "macos/Config/Version.xcconfig",
        ]
        before = {
            "schema": 1,
            "productVersion": "2.0.0",
            "windowsRevision": 0,
            "macBuild": 35,
        }
        after = {**before, "macBuild": 36}
        root = Path(".")

        def check(candidate):
            with patch(
                "sidey_tools.repository.git",
                side_effect=[json.dumps(before), json.dumps(candidate)],
            ) as git:
                result = validate_paths(
                    "macos/release", paths, root=root, base="base", revision="head",
                )
            self.assertEqual(
                git.call_args_list,
                [
                    call(root, "show", "base:release/version.json"),
                    call(root, "show", "head:release/version.json"),
                ],
            )
            return result

        self.assertEqual(check(after), "macos")
        for candidate in (
            {**after, "productVersion": "2.0.1"},
            {**after, "windowsRevision": 1},
            {**after, "schema": 2},
            {**after, "schema": True},
            {**after, "other": 1},
            {**before, "macBuild": 35},
            {**before, "macBuild": 34},
            {**before, "macBuild": True},
        ):
            with self.subTest(candidate=candidate):
                with self.assertRaisesRegex(WorkflowError, "only increase macBuild"):
                    check(candidate)
        with self.assertRaisesRegex(WorkflowError, "both generated macOS mirrors"):
            validate_paths(
                "macos/release", paths[:-1], root=root, base="base", revision="head",
            )
        with self.assertRaisesRegex(WorkflowError, "explicit root and base"):
            validate_paths("macos/release", paths)
        with self.assertRaisesRegex(WorkflowError, "platform boundary"):
            validate_paths("windows/release", paths, root=root, base="base")

    def test_ci_pr_scope_checks_macos_counter_against_pr_base_and_head(self):
        paths = [
            "release/version.json",
            "release/macos.json",
            "macos/Config/Version.xcconfig",
        ]
        event = {
            "action": "synchronize",
            "pull_request": {
                "base": {"sha": "base"},
                "head": {"sha": "head", "ref": "macos/release"},
                "title": "build(AppStore): 빌드 번호 갱신",
                "body": "body",
            },
        }
        before = {
            "schema": 1, "productVersion": "2.0.0",
            "windowsRevision": 0, "macBuild": 35,
        }
        with (
            patch("validate_change.changed_paths", return_value=paths),
            patch("validate_change.commit_messages", return_value=[]),
            patch("validate_change.verify_commit_contract"),
            patch("validate_change.verify_pr_contract"),
            patch(
                "sidey_tools.repository.git",
                side_effect=[
                    json.dumps(before),
                    json.dumps({**before, "windowsRevision": 1, "macBuild": 36}),
                ],
            ),
        ):
            with self.assertRaisesRegex(WorkflowError, "only increase macBuild"):
                resolve_change(Path("."), event)

    def test_commit_messages_preserve_bodies_and_remove_record_newlines(self):
        output = (
            'fix: 첫 번째 변경\n\n변경 이유를 설명해요.\x00\n'
            "Merge branch 'main' into shared/task\x00\n"
        )
        with patch("validate_change.git", return_value=output):
            self.assertEqual(
                commit_messages(Path("."), "base", "head"),
                [
                    "fix: 첫 번째 변경\n\n변경 이유를 설명해요.",
                    "Merge branch 'main' into shared/task",
                ],
            )

    def test_new_commit_range_and_pr_title_follow_policy(self):
        verify_commit_contract(
            [
                "chore(commit): 기여자 구조 정리",
                "Merge branch 'main' into shared/contributor-architecture",
            ],
            "chore(commit): AI 기여자 구조 최종화",
        )

    def test_invalid_new_commit_or_pr_title_fails_gate(self):
        with self.assertRaisesRegex(WorkflowError, "commit subject"):
            verify_commit_contract(["Contributor architecture cleanup"])
        with self.assertRaisesRegex(WorkflowError, "PR title"):
            verify_commit_contract(
                ["chore(commit): 기여자 구조 정리"],
                "Contributor architecture cleanup",
            )

    def test_long_new_commit_body_passes_gate(self):
        message = "docs: 설명 추가\n\n" + "긴 설명입니다. " * 30
        verify_commit_contract([message])

    def test_pr_body_requires_description_but_not_template(self):
        root = Path(__file__).parents[2]
        self.assertEqual(
            verify_pr_contract(root, "Reason and checks", ["docs/guide.md"]),
            "general",
        )
        with self.assertRaisesRegex(WorkflowError, "description"):
            verify_pr_contract(root, "<!-- comment only -->", ["docs/guide.md"])

    def test_maintainer_asset_pr_uses_general_template(self):
        root = Path(__file__).parents[2]
        general_body = (
            root / ".github/PULL_REQUEST_TEMPLATE/general.md"
        ).read_text(encoding="utf-8")
        paths = ["assets/v1/characters/capybara/idle.png"]
        self.assertEqual(
            verify_pr_contract(root, general_body, paths),
            "general",
        )
        with self.assertRaisesRegex(WorkflowError, "description"):
            verify_pr_contract(
                root,
                "<!-- SIDEY_CHARACTER_ASSET_PR_TEMPLATE: keep -->\n",
                paths,
            )

    def test_shared_branch_cannot_cross_platform_boundary(self):
        with self.assertRaisesRegex(WorkflowError, "platform boundary"):
            validate_pr_paths(
                "shared/script-relocation",
                ["scripts/macos/archive_app_store.sh"],
            )

    def test_required_job_cannot_be_skipped_missing_cancelled_or_failed(self):
        for status in ("skipped", "failure", "cancelled", None):
            needs = {
                "scope": {"result": "success"},
                "shared": {"result": "success"},
            }
            if status:
                needs["macos"] = {"result": status}
            with self.assertRaises(WorkflowError):
                verify_gate(["shared", "macos"], needs)

    def test_unaffected_platform_may_skip(self):
        verify_gate(
            ["shared"],
            {
                "scope": {"result": "success"},
                "shared": {"result": "success"},
                "windows": {"result": "skipped"},
            },
        )

    def test_edited_pr_revalidates_policy_without_scheduling_heavy_jobs(self):
        event = {
            "action": "edited",
            "pull_request": {
                "base": {"sha": "base"},
                "head": {"sha": "head", "ref": "shared/task"},
                "title": "docs: update policy",
                "body": "body",
                "labels": [],
            },
        }
        with (
            patch(
                "validate_change.changed_paths",
                return_value=["docs/guide.md"],
            ),
            patch(
                "validate_change.commit_messages",
                return_value=["docs: update policy"],
            ),
            patch("validate_change.verify_commit_contract") as commits,
            patch("validate_change.verify_pr_contract") as body,
            patch("validate_change.validate_pr_paths") as paths,
            patch("validate_change.required_scopes") as scopes,
        ):
            result = resolve_change(Path("."), event)
        self.assertEqual(result["scopes"], [])
        commits.assert_called_once_with(
            ["docs: update policy"],
            "docs: update policy",
        )
        body.assert_called_once_with(
            Path("."),
            "body",
            ["docs/guide.md"],
        )
        paths.assert_called_once_with(
            "shared/task", ["docs/guide.md"],
            root=Path("."), base="base", revision="head",
        )
        scopes.assert_not_called()

    def test_normal_pr_emits_resolved_scopes(self):
        event = {
            "action": "synchronize",
            "pull_request": {
                "base": {"sha": "base"},
                "head": {"sha": "head", "ref": "windows/task"},
                "title": "fix(Windows): update app",
                "body": "body",
                "labels": [],
            },
        }
        with (
            patch(
                "validate_change.changed_paths",
                return_value=["windows/SIDEY/App.xaml.cs"],
            ),
            patch("validate_change.commit_messages", return_value=[]),
            patch("validate_change.verify_commit_contract"),
            patch("validate_change.verify_pr_contract"),
            patch("validate_change.validate_pr_paths"),
            patch(
                "validate_change.required_scopes",
                return_value=["shared", "windows"],
            ) as scopes,
        ):
            result = resolve_change(Path("."), event)
        self.assertEqual(result["scopes"], ["shared", "windows"])
        scopes.assert_called_once_with(["windows/SIDEY/App.xaml.cs"])

    def test_empty_edited_scope_requires_only_scope_job(self):
        verify_gate(
            [],
            {
                "scope": {"result": "success"},
                "shared": {"result": "skipped"},
                "windows": {"result": "skipped"},
            },
        )

    def test_scope_failure_never_passes_empty_requirements(self):
        with self.assertRaises(WorkflowError):
            verify_gate(
                [],
                {
                    "scope": {"result": "failure"},
                    "shared": {"result": "success"},
                },
            )
