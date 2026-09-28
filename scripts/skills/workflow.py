#!/usr/bin/env python3
"""Local task ownership, fresh-main checks and reviewed integration for SIDEY.

State is local to the Git common directory. No stash, reset or force push.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import hashlib
import json
import locale
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

from sidey_tools import WorkflowError
from sidey_tools.policy import (
    GENERAL_MARKER,
    GENERAL_TEMPLATE,
    PullRequestValidationError,
    validate_message,
    validate_pr_body,
    validate_subject,
)
from sidey_tools.process import decode_output, run
from sidey_tools.repository import (
    platform_for as repository_platform_for,
    validate_paths as validate_repository_paths,
)
from validation_scope import is_contributor_architecture_path, required_scopes


# Compatibility aliases for callers that inspect the canonical general
# template.
GENERAL_PR_TEMPLATE = GENERAL_TEMPLATE
GENERAL_PR_MARKER = GENERAL_MARKER
GITHUB_REPOSITORY = 'sidey-app/SIDEY-source'
LEGACY_GITHUB_REPOSITORY = 'sidey-app/SIDEY'
# The numeric ID survives renames and rejects a stale legacy remote after the
# public distribution repository takes over the SIDEY name.
GITHUB_REPOSITORY_ID = 1350230803
LEGACY_MACOS_APP_NAMES = {'SIDEYAppStore': 'SIDEY'}


def app_names_match(recorded, requested):
    return LEGACY_MACOS_APP_NAMES.get(recorded, recorded) == requested


def git(root, *args):
    return run(root, 'git', *args)


def root_at(path):
    return Path(git(path, 'rev-parse', '--show-toplevel')).resolve()


def common_dir(root):
    return Path(
        git(
            root,
            'rev-parse',
            '--path-format=absolute',
            '--git-common-dir',
        )
    )


@contextmanager
def lock(root, name='state'):
    directory = common_dir(root) / 'sidey-workflow'
    directory.mkdir(exist_ok=True)
    with (directory / f'{name}.lock').open('a+b') as stream:
        if os.name == 'nt':
            import msvcrt
            stream.seek(0)
            stream.write(b'0')
            stream.flush()
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            yield directory
        finally:
            if os.name == 'nt':
                stream.seek(0)
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream, fcntl.LOCK_UN)


def atomic_json(path, value):
    descriptor, temporary = tempfile.mkstemp(
        dir=path.parent,
        prefix=path.name + '.',
    )
    try:
        with os.fdopen(descriptor, 'w', encoding='utf-8') as stream:
            json.dump(value, stream, indent=2, ensure_ascii=False)
            stream.write('\n')
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def read_state_file(path):
    raw = path.read_bytes()
    try:
        return json.loads(raw.decode('utf-8'))
    except UnicodeDecodeError:
        # getencoding() reports the real locale code page even when
        # Python UTF-8 mode is enabled.
        legacy_encoding = locale.getencoding()
        if legacy_encoding.lower().replace('-', '') == 'utf8':
            raise
        value = json.loads(raw.decode(legacy_encoding))
        atomic_json(path, value)
        return value


def read_state(root):
    with lock(root) as directory:
        path = directory / 'tasks.json'
        return read_state_file(path) if path.exists() else {}


def update_task(root, task_id, value):
    with lock(root) as directory:
        path = directory / 'tasks.json'
        data = read_state_file(path) if path.exists() else {}
        data[task_id] = value
        atomic_json(path, data)


def branch(root):
    return git(root, 'symbolic-ref', '--quiet', '--short', 'HEAD')


def head(root):
    return git(root, 'rev-parse', 'HEAD')


def github_repository_metadata(root, repository):
    """Return GitHub metadata used to bind a remote to a repository ID."""

    metadata = json.loads(
        run(root, 'gh', 'api', f'repos/{repository}')
    )
    if not isinstance(metadata, dict):
        raise WorkflowError(
            f'GitHub returned invalid repository metadata for {repository}'
        )
    return metadata


def canonical_remote(root):
    """Return the remote and live name of the canonical source repository."""

    remotes = git(root, 'remote').splitlines()
    for remote in ('upstream', 'origin'):
        if remote not in remotes:
            continue
        try:
            repository = github_repository_from_remote(
                root,
                remote,
                push=False,
            )
        except WorkflowError:
            continue
        allowed_names = {GITHUB_REPOSITORY, LEGACY_GITHUB_REPOSITORY}
        if repository.casefold() not in {
            name.casefold() for name in allowed_names
        }:
            continue
        metadata = github_repository_metadata(root, repository)
        if metadata.get('id') != GITHUB_REPOSITORY_ID:
            raise WorkflowError(
                f'{remote} does not identify the canonical SIDEY source '
                'repository'
            )
        live_repository = str(metadata.get('full_name') or '')
        if live_repository.casefold() not in {
            name.casefold() for name in allowed_names
        }:
            raise WorkflowError(
                'Canonical SIDEY source repository has an unexpected name: '
                f'{live_repository}'
            )
        return remote, live_repository

    if 'origin' in remotes:
        raise WorkflowError(
            'Canonical main requires a remote for '
            'sidey-app/SIDEY-source; add it as upstream when origin is a '
            'fork'
        )
    raise WorkflowError(
        'Add the SIDEY source repository as an origin or upstream remote'
    )


def main_remote(root):
    """Return the remote used to refresh canonical main."""

    return canonical_remote(root)[0]


def source_repository(root):
    """Return the canonical source repository's current GitHub name."""

    return canonical_remote(root)[1]


def fetch_main(root):
    # A stale remote-tracking ref is never proof of freshness.
    remote = main_remote(root)
    tracking_ref = f'refs/remotes/{remote}/main'
    git(
        root,
        'fetch',
        '--no-tags',
        remote,
        f'+refs/heads/main:{tracking_ref}',
    )
    return git(root, 'rev-parse', tracking_ref)


def is_ancestor(root, ancestor, descendant='HEAD'):
    result = subprocess.run(
        ['git', 'merge-base', '--is-ancestor', ancestor, descendant],
        cwd=root,
    )
    if result.returncode not in (0, 1):
        raise WorkflowError('Cannot determine commit ancestry')
    return result.returncode == 0


def same_tree(root, left, right):
    left_tree = git(root, 'rev-parse', f'{left}^{{tree}}')
    right_tree = git(root, 'rev-parse', f'{right}^{{tree}}')
    return left_tree == right_tree


def worktrees(root):
    records = []
    for record in git(root, 'worktree', 'list', '--porcelain').split('\n\n'):
        entry = dict(line.split(' ', 1) if ' ' in line else (line, True)
                     for line in record.splitlines())
        if entry:
            records.append(entry)
    return records


def primary_root(root):
    return Path(worktrees(root)[0]['worktree']).resolve()


def dirty_paths(root):
    # Disable rename detection so both sides of a move cross the
    # boundary guard.
    tracked = git(
        root,
        'diff',
        '--no-renames',
        '--name-only',
        '-z',
        'HEAD',
    ).split('\0')
    others = git(
        root,
        'ls-files',
        '--others',
        '--exclude-standard',
        '-z',
    ).split('\0')
    return sorted(set(tracked + others) - {''})


def changed_paths(root, base, revision='HEAD', dirty=False):
    merge_base = git(root, 'merge-base', base, revision)
    names = git(
        root,
        'diff',
        '--no-renames',
        '--name-only',
        '-z',
        merge_base,
        revision,
    ).split('\0')
    uncommitted = dirty_paths(root) if dirty else []
    return sorted((set(names) | set(uncommitted)) - {''})


def platform_for(path):
    """Return the platform boundary that owns *path*."""

    return repository_platform_for(path)


def validate_paths(branch_name, paths, *, root=None, base=None, revision='HEAD'):
    """Validate *paths* against the platform encoded in *branch_name*."""

    return validate_repository_paths(
        branch_name, paths, root=root, base=base, revision=revision,
    )


def app_review_required(platform, paths):
    if platform == 'shared':
        return False
    validation_workflows = {
        'macos': {
            '.github/workflows/macos-build-and-tests.yml',
            '.github/workflows/validate-macos.yml',
        },
        'windows': {
            '.github/workflows/validate-windows.yml',
            '.github/workflows/windows-build-and-tests.yml',
        },
    }[platform]
    return any(path not in validation_workflows for path in paths)


def snapshot(root):
    digest = hashlib.sha256()
    digest.update(head(root).encode())
    digest.update(git(root, 'diff', '--binary', 'HEAD').encode())
    # Include the index: staging after inspection also invalidates the
    # result.
    digest.update(git(root, 'diff', '--cached', '--binary').encode())
    for name in dirty_paths(root):
        path = root / name
        digest.update(name.encode() + b'\0')
        if path.is_symlink():
            digest.update(os.readlink(path).encode())
        elif path.is_file():
            digest.update(path.read_bytes())
        else:
            digest.update(b'<deleted>')
    return digest.hexdigest()


def owned_task(root, task_id):
    task = read_state(root).get(task_id)
    wrong_worktree = task and task['worktree'] != str(root.resolve())
    try:
        current_branch = branch(root)
    except WorkflowError:
        current_branch = None
    detached_after_merge = (
        task and current_branch is None
        and task.get('status') in ('integrated', 'main-updated', 'complete')
        and (task.get('checked') or {}).get('head') == head(root)
    )
    wrong_branch = task and not (
        task['branch'] == current_branch or detached_after_merge
    )
    if not task or wrong_worktree or wrong_branch:
        raise WorkflowError(
            'Task does not own this worktree and branch; use start in an '
            'isolated worktree'
        )
    return task


def local_checks(root, platform):
    run(root, 'git', 'diff', '--check', capture=False)
    run(
        root,
        sys.executable,
        '-X',
        'utf8',
        '-m',
        'unittest',
        'discover',
        '-s',
        'scripts/tests',
        capture=False,
    )
    run(
        root,
        sys.executable,
        '-X',
        'utf8',
        '-m',
        'unittest',
        'discover',
        '-s',
        'scripts/skills/release-notes/tests',
        capture=False,
    )
    # Platform, website and device checks are recorded separately by the
    # maintainer; GitHub Actions is disabled.
    if platform == 'shared':
        run(
            root,
            sys.executable,
            '-X',
            'utf8',
            'scripts/validate_pixel_assets.py',
            capture=False,
        )
        run(
            root,
            sys.executable,
            '-X',
            'utf8',
            'scripts/skills/verify_release_consistency.py',
            '--allow-unreleased-source',
            capture=False,
        )


def check_task(root, task_id):
    task = owned_task(root, task_id)
    remote = fetch_main(root)
    if not is_ancestor(root, remote):
        raise WorkflowError(
            'Remote main advanced; preserve your changes and run sync before '
            'checking'
        )
    paths = changed_paths(root, remote, dirty=True)
    validate_paths(branch(root), paths, root=root, base=remote)
    before = snapshot(root)
    checked_head = head(root)
    local_checks(root, task['platform'])
    source_changed = (
        fetch_main(root) != remote
        or head(root) != checked_head
        or snapshot(root) != before
    )
    if source_changed:
        raise WorkflowError(
            'Source or remote main changed during checks; results invalidated'
        )
    checked = {
        'head': checked_head,
        'snapshot': before,
        'base': remote,
        'scopes': required_scopes(paths),
        'app_review_required': app_review_required(task['platform'], paths),
        'time': time.time(),
    }
    task.update(base=remote, checked=checked, status='checked')
    update_task(root, task_id, task)
    return task


def attest(root, task, remote):
    checked = task.get('checked', {})
    invalid = (
        checked.get('head') != head(root)
        or checked.get('snapshot') != snapshot(root)
        or checked.get('base') != remote
        or not is_ancestor(root, remote)
    )
    if invalid:
        raise WorkflowError(
            'Checks do not match current source/head/base; run check again'
        )


def update_main(root, remote, already_locked=False):
    primary = primary_root(root)

    def apply():
        if branch(primary) != 'main' or dirty_paths(primary):
            raise WorkflowError(
                'PR integrated, but primary main is not clean: '
                f'{primary}; completion pending'
            )
        git(primary, 'merge', '--ff-only', remote)
        if head(primary) != remote:
            raise WorkflowError(
                'Primary main differs from fetched remote main; completion '
                'pending'
            )
    if already_locked:
        apply()
    else:
        with lock(root, 'integration'):
            apply()
    return primary


def cleanup_merged_worktrees(root, remote, *, now=None):
    """Remove old, completed task worktrees after main is updated.

    Only sibling worktrees registered by this workflow are considered. A task
    with an open PR, local content, a newer head, or unverified integration is
    left alone. Task records are retained.
    """
    cutoff = (time.time() if now is None else now) - 2 * 24 * 60 * 60
    current = root.resolve()
    primary = primary_root(root)
    entries = {
        str(Path(entry['worktree']).resolve()): entry
        for entry in worktrees(root)
    }
    repository = source_repository(root)
    removed = []
    with lock(root, 'integration'):
        for task_id, task in read_state(root).items():
            path = Path(task['worktree']).resolve()
            if (
                task.get('status') != 'complete'
                or path in (current, primary)
                or path.parent != current.parent
                or not path.is_dir()
            ):
                continue
            entry = entries.get(str(path))
            checked = task.get('checked') or {}
            merge = task.get('merge')
            if (
                not entry
                or 'locked' in entry
                or not (
                    entry.get('branch') == f"refs/heads/{task.get('branch')}"
                    or entry.get('detached') is True
                )
                or not checked.get('head')
                or entry.get('HEAD') != checked['head']
                or not merge
                or not is_ancestor(root, merge, remote)
                or not same_tree(root, checked['head'], merge)
            ):
                continue
            activity = max(
                float(git(path, 'show', '-s', '--format=%ct', 'HEAD')),
                *(float(stamp['time']) for stamp in (
                    checked, task.get('merge_intent') or {},
                    task.get('app_review') or {},
                ) if stamp.get('time') is not None),
            )
            if activity >= cutoff:
                continue
            # Include ignored outputs: removing a worktree must not silently
            # discard files produced by a developer or build.
            if git(path, 'status', '--porcelain', '--untracked-files=all',
                   '--ignored'):
                continue
            if open_task_prs(
                root, task_pr_head(path, task), repository=repository,
                base=None,
            ):
                continue
            if head(path) != checked['head']:
                continue
            git(root, 'worktree', 'remove', str(path))
            if entry.get('branch') and git(
                root, 'rev-parse', f"refs/heads/{task['branch']}",
            ) == checked['head']:
                git(root, 'branch', '-D', task['branch'])
            removed.append({'task': task_id, 'worktree': str(path)})
    return removed


def cleanup_merged_pr_branch(root, task, remote):
    """Delete the exact merged PR branch, retaining its recent worktree."""
    checked_head = (task.get('checked') or {}).get('head')
    merge = task.get('merge')
    published = task.get('published') or {}
    push_remote = published.get('push_remote')
    head_ref = published.get('head_ref')
    owner, separator, branch_name = (head_ref or '').partition(':')
    if (
        task.get('status') not in ('integrated', 'main-updated', 'complete')
        or not checked_head or head(root) != checked_head or dirty_paths(root)
        or not merge or not is_ancestor(root, merge, remote)
        or not same_tree(root, checked_head, merge)
        or not task.get('pr') or not push_remote
        or not separator or branch_name != task.get('branch')
        or github_repository_from_remote(root, push_remote).split('/')[0].casefold()
        != owner.casefold()
    ):
        raise WorkflowError('Merged PR branch lacks exact task provenance')
    repository = source_repository(root)
    info = json.loads(run(
        root, 'gh', 'pr', 'view', str(task['pr']), '--repo', repository,
        '--json', 'state,headRefOid,mergeCommit',
    ))
    if (
        info.get('state') != 'MERGED'
        or info.get('headRefOid') != checked_head
        or (info.get('mergeCommit') or {}).get('oid') != merge
        or open_task_prs(root, head_ref, repository=repository, base=None)
    ):
        raise WorkflowError('Merged PR branch is still in use or has changed')
    remote_ref = f"refs/heads/{task['branch']}"
    remote_tip = git(root, 'ls-remote', '--heads', push_remote, remote_ref)
    local_ref = f"refs/heads/{task['branch']}"
    local_refs = git(
        root, 'for-each-ref', '--format=%(refname)%09%(objectname)', local_ref,
    )
    local_tip = next((
        line.split('\t', 1)[1] for line in local_refs.splitlines()
        if line.split('\t', 1)[0] == local_ref
    ), '')
    if local_tip and local_tip != checked_head:
        raise WorkflowError('Merged PR branch local head changed')
    if any(
        entry.get('branch') == local_ref
        and Path(entry['worktree']).resolve() != root.resolve()
        for entry in worktrees(root)
    ):
        raise WorkflowError('Merged PR branch is checked out elsewhere')
    remote_deleted = False
    if remote_tip:
        if remote_tip != f'{checked_head}\t{remote_ref}':
            raise WorkflowError('Merged PR branch remote head changed')
        git(
            root, 'push', f'--force-with-lease={remote_ref}:{checked_head}',
            push_remote, f':{remote_ref}',
        )
        remote_deleted = True
    local_deleted = False
    if local_tip:
        try:
            active_branch = branch(root)
        except WorkflowError:
            active_branch = None
        if active_branch == task['branch']:
            git(root, 'switch', '--detach', checked_head)
        git(root, 'branch', '-D', task['branch'])
        local_deleted = True
    return {'remote_deleted': remote_deleted, 'local_deleted': local_deleted}


WINDOWS_SETUP_NAME = re.compile(
    r'SIDEY-Windows-x64-v\d+\.\d+\.\d+-Setup\.exe', re.IGNORECASE,
)


def build_windows_test_installer(source, output):
    """Build the maintained self-contained Setup from a checked source."""
    if os.name != 'nt':
        raise WorkflowError('Windows test Setup requires a Windows host')
    windows_root = source / 'windows'
    publish = output / 'publish'
    artifacts = output / 'artifacts'
    product_version = run(
        source, sys.executable, '-X', 'utf8',
        'scripts/sidey_version.py', '--get', 'productVersion',
    )
    release_version = run(
        source, sys.executable, '-X', 'utf8',
        'scripts/sidey_version.py', '--get', 'windowsReleaseVersion',
    )
    run(windows_root, 'dotnet', 'restore', 'SIDEY.Windows.slnx', capture=False)
    run(
        windows_root, 'dotnet', 'publish',
        'src/Sidey.App/Sidey.App.csproj', '--configuration', 'Release',
        '--runtime', 'win-x64', '--self-contained', 'true',
        '--no-restore', '-p:PublishSingleFile=false',
        '--output', str(publish), capture=False,
    )
    run(
        source, 'powershell.exe', '-NoProfile', '-NonInteractive',
        '-ExecutionPolicy', 'Bypass', '-File',
        str(source / 'scripts/windows/New-WindowsInstaller.ps1'),
        '-PublishDirectory', str(publish),
        '-OutputDirectory', str(artifacts),
        '-ProductVersion', product_version,
        '-ReleaseVersion', release_version,
        capture=False,
    )
    return artifacts / f'SIDEY-Windows-x64-v{release_version}-Setup.exe'


def file_sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def windows_downloads_folder():
    if os.name == 'nt':
        import winreg
        try:
            with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r'Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders',
            ) as key:
                value, _ = winreg.QueryValueEx(
                    key, '{374DE290-123F-4565-9164-39C4925E467B}',
                )
            return Path(os.path.expandvars(value))
        except OSError:
            pass
    return Path.home() / 'Downloads'


def publish_windows_test_installer(
    source, expected_head, downloads=None, *, base=None,
    expected_snapshot=None,
):
    """Place verified Setup in Downloads before removing older SIDEY Setups."""
    base = expected_head if base is None else base
    downloads = (downloads or windows_downloads_folder()).resolve(strict=True)
    if not downloads.is_dir():
        raise WorkflowError(f'Windows Downloads folder is missing: {downloads}')
    if (
        head(source) != expected_head
        or dirty_paths(source)
        or (expected_snapshot and snapshot(source) != expected_snapshot)
        or not is_ancestor(source, base, expected_head)
        or fetch_main(source) != base
    ):
        raise WorkflowError('Windows test Setup requires exact checked source')
    with tempfile.TemporaryDirectory(prefix='sidey-test-installer-') as raw:
        output = Path(raw).resolve()
        if output.parent != Path(tempfile.gettempdir()).resolve():
            raise WorkflowError('Installer build directory escaped temporary root')
        installer = build_windows_test_installer(source, output)
        if (
            installer.parent != output / 'artifacts'
            or not WINDOWS_SETUP_NAME.fullmatch(installer.name)
            or not installer.is_file()
            or installer.is_symlink()
        ):
            raise WorkflowError('Expected one canonical Windows Setup output')
        built_hash = file_sha256(installer)
        if (
            fetch_main(source) != base
            or head(source) != expected_head
            or dirty_paths(source)
            or (expected_snapshot and snapshot(source) != expected_snapshot)
        ):
            raise WorkflowError('Source or main changed during Windows test Setup build')
        descriptor, raw_stage = tempfile.mkstemp(
            prefix='.sidey-test-installer-', suffix='.tmp', dir=downloads,
        )
        stage = Path(raw_stage)
        try:
            with os.fdopen(descriptor, 'wb') as destination:
                with installer.open('rb') as input_stream:
                    shutil.copyfileobj(input_stream, destination)
            if stage.parent.resolve() != downloads or file_sha256(stage) != built_hash:
                raise WorkflowError('Windows test Setup copy failed verification')
            target = downloads / installer.name
            if target.parent.resolve() != downloads:
                raise WorkflowError('Windows test Setup target escaped Downloads')
            if (
                fetch_main(source) != base
                or head(source) != expected_head
                or dirty_paths(source)
                or (expected_snapshot and snapshot(source) != expected_snapshot)
            ):
                raise WorkflowError('Source or main changed before Windows Setup placement')
            os.replace(stage, target)
        finally:
            stage.unlink(missing_ok=True)
    removed = []
    for old in downloads.iterdir():
        if (
            old != target
            and WINDOWS_SETUP_NAME.fullmatch(old.name)
            and old.is_file()
            and not old.is_symlink()
            and old.parent.resolve() == downloads
        ):
            old.unlink()
            removed.append(str(old))
    return {
        'main': base, 'head': expected_head,
        'path': str(target), 'sha256': built_hash,
        'removed_installers': removed,
    }


def test_installer_task(root, task_id):
    task = owned_task(root, task_id)
    if task['platform'] != 'windows' or task.get('status') not in (
        'checked', 'published',
    ):
        raise WorkflowError('Test Setup requires a checked Windows task')
    remote = fetch_main(root)
    attest(root, task, remote)
    if dirty_paths(root):
        raise WorkflowError('Test Setup requires a clean checked worktree')
    return publish_windows_test_installer(
        root, head(root), base=remote,
        expected_snapshot=task['checked']['snapshot'],
    )


def completed_windows_installer(task, primary, remote):
    if (
        task['platform'] == 'windows'
        and task.get('status') == 'complete'
        and (task.get('app_review') or {}).get('main') == remote
    ):
        return publish_windows_test_installer(primary, remote)
    return None


def verify_windows_run(remote, metadata, jobs):
    workflow_paths = {
        '.github/workflows/ci.yml',
        '.github/workflows/validate-change.yml',
    }
    invalid_run = (
        metadata.get('head_sha') != remote
        or metadata.get('head_branch') != 'main'
        or metadata.get('event') != 'push'
        or metadata.get('status') != 'completed'
        or metadata.get('conclusion') != 'success'
        or metadata.get('path', '').split('@')[0] not in workflow_paths
    )
    if invalid_run:
        raise WorkflowError(
            'Windows app review requires a successful SIDEY CI run '
            'for current main'
        )
    windows_job_names = {
        'Windows build and tests',
        'Windows validation',
    }
    windows = [
        job
        for job in jobs
        if job.get('name') in windows_job_names
    ]
    if len(windows) != 1 or windows[0].get('conclusion') != 'success':
        raise WorkflowError(
            'Current main did not run the Windows job successfully'
        )
    steps = [
        step
        for step in windows[0].get('steps', [])
        if step.get('name') == 'Run Windows app smoke'
    ]
    if len(steps) != 1 or steps[0].get('conclusion') != 'success':
        raise WorkflowError(
            'Windows app startup/preview smoke is missing or did not pass'
        )


def recover_merged_task(root, task, remote):
    # The server may merge successfully even if the client receives a
    # timeout/503.
    checked = task.get('checked', {})
    if (
        checked.get('head') != head(root)
        or checked.get('snapshot') != snapshot(root)
        or not checked.get('head')
    ):
        return None
    intent = task.get('merge_intent', {})
    if (
        intent.get('head') != checked['head']
        or intent.get('base') != checked.get('base')
    ):
        return None
    has_qualified_head = bool(
        task.get('published', {}).get('head_ref')
    )
    if has_qualified_head:
        prs = task_prs(
            root,
            task_pr_head(root, task),
            state='merged',
        )
    else:
        # Preserve recovery for tasks published before owner-qualified
        # fork heads were recorded.
        output = run(
            root,
            'gh',
            'pr',
            'list',
            '--head',
            branch(root),
            '--base',
            'main',
            '--state',
            'merged',
            '--json',
            'number,headRefOid,mergeCommit,isCrossRepository',
        )
        prs = json.loads(output)
    matches = [
        pr
        for pr in prs
        if pr['headRefOid'] == checked['head']
        and (has_qualified_head or not pr['isCrossRepository'])
        and pr.get('mergeCommit')
        and is_ancestor(root, pr['mergeCommit']['oid'], remote)
        and same_tree(root, checked['head'], pr['mergeCommit']['oid'])
    ]
    if len(matches) != 1:
        return None
    pr = matches[0]
    return {
        **task,
        'status': 'integrated',
        'pr': str(pr['number']),
        'merge': pr['mergeCommit']['oid'],
    }


def require_valid_commit_text(label, value, *, subject_only=False):
    validator = validate_subject if subject_only else validate_message
    violations = validator(value)
    if violations:
        details = '; '.join(violations)
        raise WorkflowError(
            f'{label} violates the commit policy: {details}'
        )


def require_pr_body(
    root,
    body,
    paths,
    *,
    label='PR body',
):
    try:
        return validate_pr_body(root, body, paths, label=label)
    except PullRequestValidationError as error:
        raise WorkflowError(str(error)) from error


def require_valid_pr(root, title, body, paths):
    """Validate a PR title and template without applying commit-body limits."""

    require_valid_commit_text('PR title', title, subject_only=True)
    return require_pr_body(root, body, paths)


def require_pr_body_file(root, value, paths):
    path = Path(value)
    if not path.is_absolute():
        path = root / path
    path = path.resolve()
    try:
        body = path.read_text(encoding='utf-8')
    except (OSError, UnicodeError) as error:
        raise WorkflowError(
            f'Cannot read --body-file as UTF-8: {error}'
        ) from error
    require_pr_body(
        root,
        body,
        paths,
        label='--body-file',
    )
    return path


def github_repository_from_remote(root, remote, *, push=True):
    """Return the GitHub owner and repository for a remote."""

    arguments = ['remote', 'get-url']
    if push:
        arguments.append('--push')
    arguments.append(remote)
    url = git(root, *arguments)
    match = re.fullmatch(
        r'(?:https?://github\.com/|ssh://git@github\.com/|'
        r'git@github\.com:)'
        r'(?P<owner>[A-Za-z0-9_.-]+)/'
        r'(?P<repository>[A-Za-z0-9_.-]+?)(?:\.git)?/?',
        url,
    )
    if not match:
        raise WorkflowError(
            f'{remote} must use a github.com URL'
        )
    return f"{match['owner']}/{match['repository']}"


def publish_head(root, push_remote):
    """Return the owner-qualified pull request head for a remote."""

    repository = github_repository_from_remote(root, push_remote)
    metadata = github_repository_metadata(root, repository)
    repository_id = metadata.get('id')
    if repository_id != GITHUB_REPOSITORY_ID:
        parent = metadata.get('parent')
        source = metadata.get('source')
        related_ids = {
            candidate.get('id')
            for candidate in (parent, source)
            if isinstance(candidate, dict)
        }
        if metadata.get('private') is not True or GITHUB_REPOSITORY_ID not in related_ids:
            raise WorkflowError(
                'Push remote must be the canonical SIDEY source repository '
                'or one of its private forks'
            )
    live_repository = str(metadata.get('full_name') or '')
    if '/' not in live_repository:
        raise WorkflowError(
            f'GitHub returned an invalid repository name for {repository}'
        )
    owner, _ = live_repository.split('/', 1)
    return f'{owner}:{branch(root)}'


def create_head(head_ref):
    """Return the head form accepted by ``gh pr create``."""

    owner, separator, branch_name = head_ref.partition(':')
    canonical_owner, _ = GITHUB_REPOSITORY.split('/', 1)
    if separator and owner.casefold() == canonical_owner.casefold():
        return branch_name
    return head_ref


def task_pr_head(root, task):
    """Return the persisted or legacy pull request head."""

    canonical_owner, _ = GITHUB_REPOSITORY.split('/', 1)
    published = task.get('published', {})
    return published.get('head_ref') or f"{canonical_owner}:{task['branch']}"


def pull_request_record(pull, repository):
    """Normalize one GitHub REST pull request response."""

    head_repository = (pull['head'].get('repo') or {}).get(
        'full_name',
        '',
    )
    merge_commit = pull.get('merge_commit_sha')
    return {
        'number': pull['number'],
        'headRefOid': pull['head']['sha'],
        'isCrossRepository': (
            head_repository.casefold() != repository.casefold()
        ),
        'mergeCommit': {'oid': merge_commit} if merge_commit else None,
    }


def task_prs(root, head_ref, *, state='open', repository=None, base='main'):
    """List canonical-repository PRs for an owner-qualified head."""

    repository = repository or source_repository(root)
    api_state = 'closed' if state == 'merged' else state
    arguments = [
        'gh', 'api', '--method', 'GET', f'repos/{repository}/pulls',
        '-f', f'state={api_state}', '-f', 'per_page=100',
        '-f', f'head={head_ref}',
    ]
    if base is not None:
        arguments.extend(('-f', f'base={base}'))
    output = run(root, *arguments)
    pulls = json.loads(output)
    if state == 'merged':
        pulls = [pull for pull in pulls if pull.get('merged_at')]
    return [pull_request_record(pull, repository) for pull in pulls]


def open_task_prs(root, head_ref=None, *, repository=None, base='main'):
    """List open task PRs, including heads hosted in forks."""

    if head_ref is None:
        canonical_owner, _ = GITHUB_REPOSITORY.split('/', 1)
        head_ref = f'{canonical_owner}:{branch(root)}'
    return task_prs(root, head_ref, repository=repository, base=base)


def require_exact_task_pr(root, prs):
    if len(prs) != 1 or prs[0]['headRefOid'] != head(root):
        raise WorkflowError('PR does not identify this exact task head')
    return str(prs[0]['number'])


def merge_task_pr(
    root,
    number,
    checked_head,
    *,
    repository=None,
):
    """Request a default GitHub squash merge for the exact checked head."""

    arguments = [
        'gh',
        'pr',
        'merge',
        number,
    ]
    if repository:
        arguments.extend(('--repo', repository))
    arguments.extend(('--squash', '--match-head-commit', checked_head))
    run(root, *arguments)


def publish(root, args):
    """Push the checked task head and create its PR without merging it."""

    task = owned_task(root, args.task)
    if dirty_paths(root):
        raise WorkflowError('Publish requires a clean checked task worktree')
    repository = source_repository(root)
    remote = fetch_main(root)
    attest(root, task, remote)
    paths = changed_paths(root, remote)
    validate_paths(branch(root), paths, root=root, base=remote)
    push_remote = getattr(args, 'push_remote', None) or 'origin'
    head_ref = publish_head(root, push_remote)
    prs = open_task_prs(root, head_ref, repository=repository)
    if len(prs) > 1:
        raise WorkflowError(
            'Task branch must identify at most one pull request'
        )
    number = str(prs[0]['number']) if prs else None
    if not prs:
        if not args.title or not args.body_file:
            raise WorkflowError(
                'Provide --title and --body-file to create the task PR'
            )
        require_valid_commit_text('PR title', args.title, subject_only=True)
        body_path = require_pr_body_file(
            root,
            args.body_file,
            paths,
        )
        title = args.title
    else:
        details = json.loads(run(
            root,
            'gh',
            'pr',
            'view',
            number,
            '--repo',
            repository,
            '--json',
            'title,body',
        ))
        title = details['title']
        body = details.get('body') or ''
        require_valid_pr(root, title, body, paths)
    git(root, 'push', '-u', push_remote, branch(root))
    if not prs:
        run(
            root,
            'gh',
            'pr',
            'create',
            '--repo',
            repository,
            '--base',
            'main',
            '--head',
            create_head(head_ref),
            '--title',
            title,
            '--body-file',
            str(body_path),
        )
    prs = open_task_prs(root, head_ref, repository=repository)
    number = require_exact_task_pr(root, prs)
    details = json.loads(run(
        root,
        'gh',
        'pr',
        'view',
        number,
        '--repo',
        repository,
        '--json',
        'title,body',
    ))
    require_valid_pr(root, details['title'], details.get('body') or '', paths)
    task.update(status='published', pr=number, published={
        'head': head(root),
        'base': remote,
        'repository': repository,
        'head_ref': head_ref,
        'push_remote': push_remote,
        'time': time.time(),
    })
    update_task(root, args.task, task)
    return {'status': 'published', 'pr': number, 'head': head(root)}


def finish(root, args):
    task = owned_task(root, args.task)
    if dirty_paths(root):
        raise WorkflowError(
            'Finish requires a clean published task; commit, check and '
            'publish '
            'the exact head first'
        )
    repository = source_repository(root)
    remote = fetch_main(root)
    if task.get('status') not in ('integrated', 'main-updated', 'complete'):
        recovered = recover_merged_task(root, task, remote)
        if recovered:
            task = recovered
            update_task(root, args.task, task)
    if task.get('status') == 'complete':
        merge = task.get('merge')
        checked_head = (task.get('checked') or {}).get('head')
        if (
            not merge or not checked_head
            or not is_ancestor(root, merge, remote)
            or not same_tree(root, checked_head, merge)
        ):
            raise WorkflowError('Completed task lacks verified main integration')
        primary = update_main(root, remote)
        result = {
            'status': 'complete', 'main': str(primary), 'sha': remote,
            'branch_cleanup': cleanup_merged_pr_branch(root, task, remote),
            'removed_worktrees': cleanup_merged_worktrees(
                root, remote,
            ),
        }
        installer = completed_windows_installer(task, primary, remote)
        if installer:
            result['test_installer'] = installer
        return result
    if task.get('status') in ('integrated', 'main-updated'):
        primary = update_main(root, remote)
        needs_app_review = task.get('checked', {}).get(
            'app_review_required', task['platform'] != 'shared')
        task['status'] = 'main-updated' if needs_app_review else 'complete'
        if args.windows_run:
            if task['platform'] != 'windows':
                raise WorkflowError(
                    '--windows-run applies only to an integrated Windows task'
                )
            run_path = (
                f'repos/{repository}/actions/runs/'
                f'{args.windows_run}'
            )
            metadata = json.loads(run(root, 'gh', 'api', run_path))
            jobs = json.loads(
                run(root, 'gh', 'api', f'{run_path}/jobs?per_page=100')
            )
            verify_windows_run(remote, metadata, jobs['jobs'])
            main_changed = (
                fetch_main(root) != remote
                or head(primary) != remote
                or dirty_paths(primary)
            )
            if main_changed:
                raise WorkflowError('Main changed during Windows app review')
            app_review = {
                'main': remote,
                'environment': 'GitHub Actions Windows',
                'run': args.windows_run,
                'url': metadata['html_url'],
                'time': time.time(),
            }
            task.update(status='complete', app_review=app_review)
        update_task(root, args.task, task)
        result = {
            'status': task['status'], 'main': str(primary), 'sha': remote,
            'branch_cleanup': cleanup_merged_pr_branch(root, task, remote),
            'removed_worktrees': cleanup_merged_worktrees(
                root, remote,
            ),
        }
        installer = completed_windows_installer(task, primary, remote)
        if installer:
            result['test_installer'] = installer
        return result
    attest(root, task, remote)
    validate_paths(
        branch(root), changed_paths(root, remote), root=root, base=remote,
    )
    paths = changed_paths(root, remote)
    prs = open_task_prs(
        root,
        task_pr_head(root, task),
        repository=repository,
    )
    if not prs:
        raise WorkflowError('No task PR exists; run publish before finish')
    number = require_exact_task_pr(root, prs)
    checked_head = task['checked']['head']
    checked_base = task['checked']['base']
    details = json.loads(
        run(
            root,
            'gh',
            'pr',
            'view',
            number,
            '--repo',
            repository,
            '--json',
            'title,body',
        )
    )
    pr_body = details.get('body') or ''
    require_valid_pr(root, details['title'], pr_body, paths)
    with lock(root, 'integration'):
        remote = fetch_main(root)
        source = json.loads(
            run(
                root,
                'gh',
                'pr',
                'view',
                number,
                '--repo',
                repository,
                '--json',
                'title,body,headRefOid,baseRefName,mergeStateStatus',
            )
        )
        attest(root, task, remote)
        source_changed = (
            source['headRefOid'] != checked_head
            or source['baseRefName'] != 'main'
            or source['mergeStateStatus'] != 'CLEAN'
            or source['title'] != details['title']
            or source.get('body') != details.get('body')
        )
        if source_changed:
            raise WorkflowError(
                'PR head/base/content is not the exact checked and '
                'mergeable source'
            )
        task['merge_intent'] = {
            'head': checked_head,
            'base': checked_base,
            'pr': number,
            'time': time.time(),
        }
        update_task(root, args.task, task)
        merge_task_pr(
            root,
            number,
            checked_head,
            repository=repository,
        )
        info = json.loads(
            run(
                root,
                'gh',
                'pr',
                'view',
                number,
                '--repo',
                repository,
                '--json',
                'state,mergeCommit,headRefOid',
            )
        )
        if info['state'] != 'MERGED' or info['headRefOid'] != checked_head:
            raise WorkflowError('Exact checked head was not confirmed merged')
        remote = fetch_main(root)
        merge_commit = info['mergeCommit']['oid']
        if (
            not is_ancestor(root, merge_commit, remote)
            or not same_tree(root, checked_head, merge_commit)
        ):
            raise WorkflowError(
                'Merged commit does not match the exact checked task tree'
            )
        task.update(status='integrated', pr=number, merge=merge_commit)
        update_task(root, args.task, task)
        primary = update_main(root, remote, already_locked=True)
    needs_app_review = task.get('checked', {}).get(
        'app_review_required', task['platform'] != 'shared')
    task['status'] = 'main-updated' if needs_app_review else 'complete'
    update_task(root, args.task, task)
    result = {
        'status': task['status'],
        'pr': number,
        'main': str(primary),
        'sha': remote,
        'branch_cleanup': cleanup_merged_pr_branch(root, task, remote),
        'removed_worktrees': cleanup_merged_worktrees(
            root, remote,
        ),
    }
    if needs_app_review:
        result['app'] = (
            'App verification is a separate required step because app inputs '
            'changed'
        )
    installer = completed_windows_installer(task, primary, remote)
    if installer:
        result['test_installer'] = installer
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', default='.')
    subs = parser.add_subparsers(dest='command', required=True)
    subs.add_parser('doctor')
    start = subs.add_parser('start')
    start.add_argument('task')
    start.add_argument(
        '--platform',
        choices=['shared', 'macos', 'windows'],
        required=True,
    )
    start.add_argument('--worktree', required=True)
    start.add_argument(
        '--app',
        default='SIDEY',
        choices=['SIDEY', 'sidey-reals', 'windows'],
    )
    test_installer = subs.add_parser('test-installer')
    test_installer.add_argument('task')
    for command in ('sync', 'check', 'publish', 'finish'):
        sub = subs.add_parser(command)
        sub.add_argument('task', nargs='?')
        if command == 'check':
            sub.add_argument('--ci', action='store_true')
            sub.add_argument('--base')
            sub.add_argument('--head', default='HEAD')
            sub.add_argument('--branch')
        if command == 'publish':
            sub.add_argument('--title')
            sub.add_argument('--body-file')
            sub.add_argument('--push-remote', default='origin')
        if command == 'finish':
            sub.add_argument(
                '--windows-run',
                type=int,
                help=(
                    'Complete an integrated Windows task using current-main '
                    'app smoke validation'
                ),
            )
    opener = subs.add_parser('open')
    opener.add_argument(
        '--task',
        help=(
            'Complete an integrated macOS task after verified latest-main '
            'app review'
        ),
    )
    opener.add_argument('--preview', type=Path)
    opener.add_argument('--offline', action='store_true')
    opener.add_argument(
        '--scheme',
        default='SIDEY',
        choices=['SIDEY', 'sidey-reals'],
    )
    args = parser.parse_args(argv)
    root = root_at(args.repo)
    if args.command == 'doctor':
        try:
            remote = fetch_main(root)
            freshness = 'verified'
        except WorkflowError:
            remote, freshness = None, 'unverified: remote fetch failed'
        entries = worktrees(root)
        for entry in entries:
            path = Path(entry['worktree'])
            entry['exists'] = path.exists()
            entry['changes'] = dirty_paths(path) if path.exists() else None
            entry['included_in_main'] = (
                is_ancestor(root, entry['HEAD'], remote)
                if remote
                else None
            )
        result = {
            'remote_main': remote,
            'freshness': freshness,
            'worktrees': entries,
        }
    elif args.command == 'start':
        if not re.fullmatch(r'[a-z0-9][a-z0-9._-]*', args.task):
            raise WorkflowError(
                'Task ID must contain lowercase letters, digits, dots, '
                'underscores or hyphens'
            )
        remote = fetch_main(root)
        destination = Path(args.worktree).resolve()
        name = f'{args.platform}/{args.task}'
        with lock(root) as directory:
            path = directory / 'tasks.json'
            data = read_state_file(path) if path.exists() else {}
            existing_worktree = any(
                task['worktree'] == str(destination)
                for task in data.values()
            )
            if args.task in data or existing_worktree:
                raise WorkflowError(
                    'Task/worktree already registered; resume using '
                    'sync/check'
                )
            if destination.exists():
                raise WorkflowError(
                    'start requires a new worktree directory; existing work '
                    'is preserved'
                )
            git(root, 'worktree', 'add', '-b', name, str(destination), remote)
            data[args.task] = {
                'worktree': str(destination),
                'branch': name,
                'platform': args.platform,
                'app': args.app,
                'base': remote,
                'status': 'started',
            }
            atomic_json(path, data)
        result = data[args.task]
    elif args.command == 'sync':
        task = owned_task(root, args.task)
        remote = fetch_main(root)
        if dirty_paths(root):
            raise WorkflowError(
                'Preserve changes in an explicit task commit before sync; '
                'no automatic stash'
            )
        validate_paths(
            branch(root), changed_paths(root, remote), root=root, base=remote,
        )
        git(root, 'merge', '--no-edit', remote)
        task.update(base=remote, status='started')
        task.pop('checked', None)
        update_task(root, args.task, task)
        result = task
    elif args.command == 'check' and args.ci:
        if not args.base or not args.branch:
            raise WorkflowError('CI requires explicit --base and --branch')
        paths = changed_paths(root, args.base, args.head)
        validate_paths(
            args.branch, paths, root=root, base=args.base, revision=args.head,
        )
        result = {'paths': paths, 'scopes': required_scopes(paths)}
    elif args.command == 'check':
        result = check_task(root, args.task)
    elif args.command == 'test-installer':
        result = test_installer_task(root, args.task)
    elif args.command == 'publish':
        result = publish(root, args)
    elif args.command == 'finish':
        result = finish(root, args)
    else:
        if args.task and (args.offline or args.preview):
            raise WorkflowError(
                'Task completion requires latest main; previews remain '
                'incomplete'
            )
        if args.offline and not args.preview:
            raise WorkflowError(
                'Offline mode requires an explicit --preview worktree'
            )
        target = root_at(args.preview) if args.preview else primary_root(root)
        if not args.offline:
            remote = fetch_main(root)
            if not args.preview:
                target = update_main(root, remote)
            elif not is_ancestor(target, remote):
                raise WorkflowError(
                    'Preview is behind current remote main; sync first or '
                    'explicitly use --offline'
                )
        script = target / 'scripts/macos/open_current.sh'
        if not script.exists():
            raise WorkflowError(
                'macOS verified opener is not installed at this revision; '
                'app was not opened'
            )
        command = [
            str(script),
            '--worktree',
            str(target),
            '--scheme',
            args.scheme,
        ]
        if args.offline:
            command.append('--offline')
        task = read_state(root).get(args.task) if args.task else None
        # Squash integration preserves the checked tree, not the branch
        # head's ancestry.
        invalid_task = args.task and (
            not task
            or task.get('status') != 'main-updated'
            or task.get('platform') != 'macos'
            or not app_names_match(task.get('app'), args.scheme)
            or not task.get('merge')
            or not task.get('checked', {}).get('head')
            or not is_ancestor(root, task['merge'], remote)
            or not same_tree(
                root,
                task['checked']['head'],
                task['merge'],
            )
        )
        if invalid_task:
            raise WorkflowError(
                'Task must be integrated and awaiting its selected macOS '
                'app review'
            )
        run(target, *command, capture=False)
        if task:
            if fetch_main(root) != remote or head(target) != remote:
                raise WorkflowError(
                    'Main changed during review; task completion remains '
                    'pending'
                )
            app_review = {
                'main': remote,
                'scheme': args.scheme,
                'source': str(target),
                'time': time.time(),
            }
            task.update(status='complete', app_review=app_review)
            update_task(root, args.task, task)
        result = {
            'target': str(target),
            'freshness': 'unverified' if args.offline else 'verified',
        }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (WorkflowError, OSError, ValueError) as error:
        print(f'workflow: {error}', file=sys.stderr)
        sys.exit(1)
