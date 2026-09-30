#!/usr/bin/env python3
"""Run every discovered unittest in isolated, bounded worker processes."""

from __future__ import annotations

import argparse
from concurrent.futures import ProcessPoolExecutor
import io
import multiprocessing
import os
from pathlib import Path
import sys
import time
import unittest


def test_ids(suite):
    for test in suite:
        if isinstance(test, unittest.TestSuite):
            yield from test_ids(test)
        else:
            yield test.id()


def run_chunk(arguments):
    directory, names = arguments
    sys.path.insert(0, directory)
    loader = unittest.TestLoader()
    suite = loader.loadTestsFromNames(names)
    output = io.StringIO()
    result = unittest.TextTestRunner(stream=output).run(suite)
    return {
        'tests': result.testsRun,
        'failures': len(result.failures),
        'errors': len(result.errors),
        'skipped': len(result.skipped),
        'expected_failures': len(result.expectedFailures),
        'unexpected_successes': len(result.unexpectedSuccesses),
        'success': result.wasSuccessful() and not loader.errors,
        'output': output.getvalue(),
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--start-directory', default='scripts/tests')
    parser.add_argument('--pattern', default='test_*.py')
    parser.add_argument('--workers', type=int, choices=range(1, 5),
                        default=min(4, os.cpu_count() or 1))
    args = parser.parse_args(argv)
    directory = str(Path(args.start_directory).resolve())
    loader = unittest.TestLoader()
    try:
        suite = loader.discover(directory, pattern=args.pattern)
    except Exception as error:
        print(f'Common test discovery failed: {error}', file=sys.stderr)
        return 1
    if loader.errors:
        print('Common test discovery failed:', file=sys.stderr)
        print('\n'.join(loader.errors), file=sys.stderr)
        return 1
    names = list(test_ids(suite))
    if not names:
        print('Common test discovery found no tests.', file=sys.stderr)
        return 1

    workers = min(args.workers, len(names))
    chunks = [(directory, names[index::workers]) for index in range(workers)]
    print(f'Discovered {len(names)} common tests; starting {workers} workers.',
          file=sys.stderr, flush=True)
    started = time.monotonic()
    try:
        with ProcessPoolExecutor(
            max_workers=workers, mp_context=multiprocessing.get_context('spawn'),
        ) as executor:
            results = list(executor.map(run_chunk, chunks))
    except Exception as error:
        print(f'Common test worker failed: {error}', file=sys.stderr)
        return 1
    for index, result in enumerate(results, 1):
        print(f"Worker {index}/{workers}: ran {result['tests']}, "
              f"failures={result['failures']}, errors={result['errors']}, "
              f"skipped={result['skipped']}", file=sys.stderr)
        print(result['output'], end='', file=sys.stderr)
    totals = {key: sum(result[key] for result in results) for key in (
        'tests', 'failures', 'errors', 'skipped',
        'expected_failures', 'unexpected_successes',
    )}
    success = (totals['tests'] == len(names)
               and all(result['success'] for result in results))
    print(
        f"Common tests: ran {totals['tests']}/{len(names)} in "
        f"{time.monotonic() - started:.3f}s with {workers} workers; "
        f"failures={totals['failures']}, errors={totals['errors']}, "
        f"skipped={totals['skipped']}, "
        f"expected failures={totals['expected_failures']}, "
        f"unexpected successes={totals['unexpected_successes']}",
        file=sys.stderr,
    )
    print('OK' if success else 'FAILED', file=sys.stderr)
    return 0 if success else 1


if __name__ == '__main__':
    raise SystemExit(main())
