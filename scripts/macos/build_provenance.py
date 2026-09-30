#!/usr/bin/env python3
"""Build input stamps and one-use Debug launch tickets (also used by Xcode)."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[2]
INPUT_DIRS = ('macos/SIDEY', 'macos/Config', 'macos/SIDEY.xcodeproj', 'scripts/macos', 'macos/Recording')
EXCLUDED = {'xcuserdata', 'build', 'DerivedData', '__pycache__', '.git', 'node_modules', 'dist'}


def command(*args):
    return subprocess.check_output(args, cwd=ROOT, text=True).rstrip('\n')


def inputs(root=ROOT):
    result = []
    for directory in INPUT_DIRS:
        base = root / directory
        if not base.exists():
            continue
        for current, dirs, files in os.walk(base, followlinks=False):
            dirs[:] = sorted(d for d in dirs if d not in EXCLUDED and not d.startswith('.'))
            if any((Path(current) / d).is_symlink() for d in dirs):
                raise RuntimeError('Symlink directory is not a reproducible build input')
            for name in sorted(files):
                path = Path(current) / name
                if name.startswith('.') or name.endswith(('.xcuserstate', '.pyc')):
                    continue
                if path.is_symlink():
                    raise RuntimeError(f'Symlink is not a reproducible build input: {path.relative_to(root)}')
                result.append(path)
    return sorted(result)


def source_state(root=ROOT):
    digest = hashlib.sha256()
    paths = inputs(root)
    for path in paths:
        digest.update(path.relative_to(root).as_posix().encode() + b'\0')
        digest.update(hashlib.sha256(path.read_bytes()).digest())
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
    status = subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=all', '--',
                                      *INPUT_DIRS], cwd=root, text=True)
    return {'commit': commit, 'input_hash': digest.hexdigest(), 'dirty': bool(status)}


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex)
    temporary.write_text(json.dumps(value, indent=2) + '\n')
    temporary.replace(path)


def claim_build_directory(directory, root, target, configuration):
    # Shared package caches are fine; executable products must have one source/target owner.
    import fcntl
    directory.mkdir(parents=True, exist_ok=True)
    identity = {'source': str(root.resolve()), 'target': target, 'configuration': configuration}
    with (directory / '.sidey-build-owner.lock').open('a+b') as stream:
        fcntl.flock(stream, fcntl.LOCK_EX)
        owner = directory / '.sidey-build-owner.json'
        if owner.exists() and json.loads(owner.read_text()) != identity:
            raise RuntimeError('DerivedData products belong to another worktree/target/configuration; use an isolated build directory')
        write_json(owner, identity)


def begin():
    env = os.environ
    claim_build_directory(Path(env['TARGET_BUILD_DIR']), ROOT, env['TARGET_NAME'], env['CONFIGURATION'])
    state = source_state()
    state.update(schema=1, build_id=str(uuid.uuid4()), target=env['TARGET_NAME'],
                 configuration=env['CONFIGURATION'], bundle_id=env['PRODUCT_BUNDLE_IDENTIFIER'])
    # Allowlist build settings; credentials, service URLs and signing secrets never enter metadata.
    state['settings'] = {key: env.get(key, '') for key in (
        'ARCHS', 'SDK_VERSION', 'XCODE_VERSION_ACTUAL', 'SWIFT_VERSION',
        'SWIFT_OPTIMIZATION_LEVEL', 'SWIFT_ACTIVE_COMPILATION_CONDITIONS',
        'MACOSX_DEPLOYMENT_TARGET', 'MARKETING_VERSION', 'CURRENT_PROJECT_VERSION')}
    derived = Path(env['DERIVED_FILE_DIR'])
    write_json(derived / 'SideyBuildReceipt.json', state)
    literal = lambda value: json.dumps(value, ensure_ascii=False)
    # The running executable carries immutable metadata, independent of the bundle on disk.
    source = 'enum SideyBuildStamp {\n' + '\n'.join(
        f'    static let {key} = {literal(value)}' for key, value in {
            'buildID': state['build_id'], 'commit': state['commit'], 'inputHash': state['input_hash'],
            'target': state['target'], 'configuration': state['configuration'],
            'bundleID': state['bundle_id'], 'dirty': state['dirty']}.items()) + '\n}\n'
    (derived / 'SideyBuildStamp.swift').write_text(source)


def finish():
    env = os.environ
    state = json.loads((Path(env['DERIVED_FILE_DIR']) / 'SideyBuildReceipt.json').read_text())
    current = source_state()
    if any(current[key] != state[key] for key in current):
        raise RuntimeError('Build inputs changed during compilation; rebuild before reviewing')
    destination = Path(env['TARGET_BUILD_DIR']) / env['UNLOCALIZED_RESOURCES_FOLDER_PATH']
    write_json(destination / 'SideyBuildReceipt.json', state)
    # Xcode's Run pre-action does not reliably prepare a ticket for the newly
    # compiled build. Issue it after the executable and receipt are produced.
    entitlements_path = env.get('CODE_SIGN_ENTITLEMENTS')
    if state['configuration'] == 'Debug' and entitlements_path:
        entitlements = plistlib.loads((Path(env['SRCROOT']) / entitlements_path).read_bytes())
        app = Path(env['TARGET_BUILD_DIR']) / env['FULL_PRODUCT_NAME']
        executable = app / 'Contents/MacOS' / env['EXECUTABLE_NAME']
        directory = directory_for_bundle(state['bundle_id'],
                                         bool(entitlements.get('com.apple.security.app-sandbox')))
        issue_ticket(app, state, executable, directory)


def read_build(app):
    receipt = json.loads((app / 'Contents/Resources/SideyBuildReceipt.json').read_text())
    info = plistlib.loads((app / 'Contents/Info.plist').read_bytes())
    if receipt['bundle_id'] != info['CFBundleIdentifier']:
        raise RuntimeError('Bundle identifier does not match compiled build metadata')
    return receipt, info


def verify(app, root=ROOT, target=None, configuration=None):
    receipt, info = read_build(app)
    current = source_state(root)
    if any(receipt[key] != current[key] for key in current):
        raise RuntimeError('App is stale for this source/commit; build again')
    if target and target != receipt['target']:
        raise RuntimeError('App was built for a different target')
    if configuration and configuration != receipt['configuration']:
        raise RuntimeError('App was built with a different configuration')
    return receipt, info


def directory_for_bundle(bundle_id, sandboxed):
    home = Path.home()
    if sandboxed:
        home /= f'Library/Containers/{bundle_id}/Data'
    return home / 'Library/Application Support/SIDEY/BuildReview'


def ticket_directory(app, bundle_id):
    # The sandboxed process reads only its own container. It never reads the repository.
    result = subprocess.run(['/usr/bin/codesign', '-d', '--entitlements', '-', str(app)],
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    if result.returncode:
        raise RuntimeError(f'Cannot inspect app entitlements: {app}')
    try:
        entitlements = plistlib.loads(result.stdout)
    except Exception:
        # Current codesign renders entitlements as a human-readable dictionary by default.
        # The former ":-" output can be empty even for a sandboxed app.
        rendered = result.stdout.decode('utf-8')
        if rendered and not rendered.startswith('[Dict]'):
            raise RuntimeError(f'Unrecognized app entitlements: {app}')
        match = re.search(r'(?m)^\s*\[Key\] com\.apple\.security\.app-sandbox\s*\n'
                          r'\s*\[Value\]\s*\n\s*\[Bool\] (true|false)\s*$', rendered)
        if 'com.apple.security.app-sandbox' in rendered and match is None:
            raise RuntimeError(f'Cannot determine app sandbox entitlement: {app}')
        entitlements = {'com.apple.security.app-sandbox': match.group(1) == 'true'} if match else {}
    return directory_for_bundle(bundle_id, bool(entitlements.get('com.apple.security.app-sandbox')))


def issue_ticket(app, receipt, executable, directory):
    session = str(uuid.uuid4())
    ticket = {**receipt, 'session': session, 'executable': str(executable.resolve()),
              'issued_at': time.time()}
    ticket_path = directory / f"{receipt['build_id']}.ticket.json"
    ready_path = directory / f'{session}.ready.json'
    write_json(ticket_path, ticket)
    write_json(app.parent / 'SideyLastLaunch.json', {'ticket': ticket, 'ready': str(ready_path)})
    return ticket, ready_path


def prepare_launch(app, target=None):
    receipt, info = verify(app, target=target, configuration='Debug')
    directory = ticket_directory(app, receipt['bundle_id'])
    executable = str((app / 'Contents/MacOS' / info['CFBundleExecutable']).resolve())
    return issue_ticket(app, receipt, Path(executable), directory)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['begin', 'finish', 'verify', 'prepare-launch', 'source'])
    parser.add_argument('--app', type=Path)
    parser.add_argument('--target')
    args = parser.parse_args()
    if args.command == 'begin':
        begin()
    elif args.command == 'finish':
        finish()
    elif args.command == 'source':
        print(json.dumps(source_state()))
    elif args.command == 'verify':
        print(json.dumps(verify(args.app, target=args.target)[0]))
    else:
        app = args.app or Path(os.environ['TARGET_BUILD_DIR']) / os.environ['FULL_PRODUCT_NAME']
        ticket, ready = prepare_launch(app, args.target or os.environ.get('TARGET_NAME'))
        print(json.dumps({'session': ticket['session'], 'ready': str(ready)}))


if __name__ == '__main__':
    main()
