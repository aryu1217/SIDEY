#!/usr/bin/env python3
"""Install SIDEY's attribution and main-push hooks for all worktrees."""

from pathlib import Path
import subprocess


HOOKS = (
    ('prepare_commit_msg.py', 'prepare-commit-msg', b'# SIDEY Codex attribution hook v1'),
    ('pre_push.py', 'pre-push', b'# SIDEY main push guard hook v1'),
)


def install():
    directory = Path(__file__).resolve().parent
    config = subprocess.run(
        ['git', 'config', '--get', 'core.hooksPath'],
        capture_output=True,
        text=True,
    )
    if config.returncode not in (0, 1):
        raise SystemExit(config.stderr)
    if config.returncode == 0:
        raise SystemExit(
            "Existing core.hooksPath is preserved. Connect SIDEY's "
            'prepare-commit-msg and pre-push hooks through that hook manager.'
        )
    common = Path(
        subprocess.check_output(
            ['git', 'rev-parse', '--git-common-dir'], text=True
        ).strip()
    ).resolve()
    targets = [
        (directory / source_name, common / 'hooks' / target_name, marker)
        for source_name, target_name, marker in HOOKS
    ]
    for _, destination, marker in targets:
        if destination.is_symlink() or (
            destination.exists()
            and marker not in destination.read_bytes().splitlines()[:3]
        ):
            raise SystemExit(
                f'Existing {destination.name} hook is preserved. Connect '
                "SIDEY's hook through its existing hook manager."
            )
    for source, destination, _ in targets:
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(source.read_bytes())
        destination.chmod(0o755)
    print(f'Installed SIDEY hooks in {common / "hooks"} for all worktrees.')


if __name__ == '__main__':
    install()
