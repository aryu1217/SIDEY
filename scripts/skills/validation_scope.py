#!/usr/bin/env python3
"""Resolve the validation jobs affected by a repository change."""
from __future__ import annotations

import re


ALL_SCOPES = frozenset({'shared', 'macos', 'windows', 'web'})

CONTRIBUTOR_ARCHITECTURE_FILES = frozenset({
    'scripts/skills/commit/prepare_commit_msg.py',
    'scripts/skills/commit/setup_codex_attribution.py',
    'scripts/skills/commit/validate_commit_message.py',
    'scripts/skills/create-pr/validate_pull_request.py',
    'scripts/skills/validate_contributor_architecture.py',
    'scripts/tests/test_codex_attribution.py',
    'scripts/tests/test_contributor_architecture.py',
    'scripts/tests/test_validate_commit_message.py',
    'scripts/tests/test_validate_pull_request.py',
})

FULL_VALIDATION_PATHS = frozenset({
    '.github/workflows/ci.yml',
    '.github/workflows/validate-change.yml',
    'scripts/skills/validation_scope.py',
})


def is_contributor_architecture_path(path):
    """Return whether *path* affects contributors but not shipped artifacts."""

    return (
        path == 'AGENTS.md'
        or path.endswith('/AGENTS.md')
        or path.startswith('.agents/skills/')
        or '/.agents/skills/' in path
        or path.startswith((
            'scripts/skills/commit/',
            'scripts/skills/create-pr/',
            'scripts/skills/release-notes/',
        ))
        or path in CONTRIBUTOR_ARCHITECTURE_FILES
    )


def is_platform_documentation_path(path):
    return path.startswith(('macos/docs/', 'windows/docs/'))


def validation_platform_for(path):
    """Return the shipped platform affected by *path*, if any.

    This intentionally differs from branch ownership. Platform documentation
    and contributor instructions stay owned by their platform while requiring
    only repository validation.
    """

    contributor_only = is_contributor_architecture_path(path)
    platform_documentation = is_platform_documentation_path(path)
    if contributor_only or platform_documentation:
        return None
    if (
        path.startswith(('macos/', 'scripts/macos/'))
        or re.fullmatch(
            r'\.github/workflows/'
            r'(?:macos-build-and-tests|validate-macos)\.yml',
            path,
        )
    ):
        return 'macos'
    if (
        path.startswith(('windows/', 'scripts/windows/'))
        or re.fullmatch(
            r'\.github/workflows/'
            r'(?:publish-windows-release|validate-windows|'
            r'windows-build-and-tests|windows-release)\.yml',
            path,
        )
    ):
        return 'windows'
    return None


def required_scopes(paths):
    """Return deterministic job IDs required to validate *paths*."""

    result = {'shared'}
    changed = set(paths)
    windows_build_only = (
        {'release/version.json', 'release/windows.json', 'windows/Version.props'}
        <= changed
        and not {'release/macos.json', 'macos/Config/Version.xcconfig',
                 'release/app-store-localizations.json'} & changed
    )
    mac_build_only = (
        {'release/version.json', 'release/macos.json',
         'macos/Config/Version.xcconfig'}
        <= changed
        and not {'release/windows.json', 'windows/Version.props'} & changed
    )
    for path in paths:
        platform = validation_platform_for(path)
        if platform:
            result.add(platform)
        asset_change = path.startswith(
            ('assets/', 'shared/character-throw/')
        )
        if asset_change or path == 'scripts/validate_pixel_assets.py':
            result.update(('macos', 'windows', 'web'))
        if path == 'release/version.json':
            if windows_build_only:
                result.update(('windows', 'web'))
            elif mac_build_only:
                result.add('macos')
            else:
                result.update(('macos', 'windows', 'web'))
        elif path == 'release/macos.json':
            result.add('macos')
        elif path == 'release/windows.json':
            result.add('windows')
        if (
            path.startswith(('website/', 'scripts/pages/'))
            and not is_contributor_architecture_path(path)
        ):
            result.add('web')
        if path == 'website/src/pages/ko/terms.md':
            result.add('windows')
        # Checkout attributes can change source bytes on every build
        # host.
        if path == '.gitattributes' or path in FULL_VALIDATION_PATHS:
            result.update(('macos', 'windows', 'web'))
        elif path in {
            '.github/workflows/deploy-website.yml',
            '.github/workflows/website-deployment.yml',
        }:
            result.add('web')
    return sorted(result)
