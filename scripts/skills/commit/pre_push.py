#!/usr/bin/env python3
# SIDEY main push guard hook v1
"""Reject direct pushes to main; reviewed PR merges use GitHub's API."""

import sys


# Git supplies fully qualified destination refs on stdin, including for
# deletions, force pushes, --all, and --mirror.
MAIN_REF = 'refs/heads/main'


def blocked_updates(lines):
    for line in lines:
        fields = line.split()
        if len(fields) != 4:
            raise ValueError('Unexpected Git pre-push update format')
        if fields[2] == MAIN_REF:
            yield line.rstrip('\n')


def main():
    try:
        blocked = list(blocked_updates(sys.stdin))
    except ValueError as error:
        print(f'SIDEY pre-push guard: {error}', file=sys.stderr)
        return 1
    if blocked:
        print(
            'SIDEY pre-push guard: direct pushes to main are blocked. '
            'Push a task branch and merge its reviewed PR instead.',
            file=sys.stderr,
        )
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
