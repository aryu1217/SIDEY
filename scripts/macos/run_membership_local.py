#!/usr/bin/env python3
"""Launch an exact Debug SIDEY build against an isolated local Supabase stack."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time

import build_provenance


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, required=True)
    parser.add_argument('--stack', type=Path, required=True)
    parser.add_argument('--slot', type=int, choices=range(1, 5), required=True)
    args = parser.parse_args()

    app = args.app.resolve()
    stack = args.stack.resolve()
    if not (stack / 'supabase/config.toml').is_file():
        raise RuntimeError('Expected an isolated local Supabase project')
    receipt, _ = build_provenance.verify(app, target='SIDEY', configuration='Debug')
    if receipt['target'] != 'SIDEY':
        raise RuntimeError('The selected app is not the SIDEY Debug target')
    status = json.loads(subprocess.check_output(
        ['supabase', 'status', '--output', 'json'], cwd=stack, text=True,
        stderr=subprocess.DEVNULL,
    ))
    url = status.get('API_URL')
    key = status.get('PUBLISHABLE_KEY') or status.get('ANON_KEY')
    if url != 'http://127.0.0.1:61321' or not key:
        raise RuntimeError('Expected the isolated membership test stack on port 61321')

    ticket, ready_path = build_provenance.prepare_launch(app, target='SIDEY')
    environment = os.environ.copy()
    environment.update(
        SIDEY_STAGING='1',
        SIDEY_LOCAL_MEMBERSHIP_TEST='1',
        SIDEY_LOCAL_CLIENT_SLOT=str(args.slot),
        SIDEY_SUPABASE_URL=url,
        SIDEY_SUPABASE_PUBLISHABLE_KEY=key,
        SIDEY_REALTIME_TRANSPORT='legacy',
    )
    logs = stack / 'logs'
    logs.mkdir(exist_ok=True)
    log_path = logs / f'client-{args.slot}.log'
    with log_path.open('ab') as stream:
        process = subprocess.Popen(
            [ticket['executable']], env=environment, stdout=stream,
            stderr=subprocess.STDOUT, start_new_session=True,
        )
    for _ in range(400):
        if ready_path.exists():
            ready = json.loads(ready_path.read_text())
            if (ready.get('session') == ticket['session']
                    and ready.get('build_id') == receipt['build_id']
                    and ready.get('pid') == process.pid
                    and ready.get('window_ready') is True):
                print(json.dumps({
                    'slot': args.slot, 'pid': process.pid, 'backend': url,
                    'build_id': receipt['build_id'], 'source': str(build_provenance.ROOT),
                    'log': str(log_path), 'window_ready': True,
                }, ensure_ascii=False, indent=2))
                return
            raise RuntimeError('Ready window did not match this exact launch')
        if process.poll() is not None:
            raise RuntimeError(f'SIDEY exited before ready window; see {log_path}')
        time.sleep(0.1)
    raise RuntimeError(f'SIDEY did not report a ready window; see {log_path}')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f'Local membership launch failed: {error}', file=sys.stderr)
        raise SystemExit(1)
