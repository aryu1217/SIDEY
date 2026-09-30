"""Verify the common runner's process and failure contracts end to end."""

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


RUNNER = Path(__file__).resolve().parents[1] / 'skills/run_common_tests.py'


class CommonTestRunnerTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)

    def run_fixture(self, workers=2):
        return subprocess.run(
            [sys.executable, '-X', 'utf8', str(RUNNER),
             '--start-directory', str(self.directory),
             '--workers', str(workers)],
            capture_output=True, text=True, encoding='utf-8', timeout=60,
        )

    def test_every_case_runs_once_with_one_or_multiple_workers(self):
        fixture = self.directory / 'test_fixture.py'
        fixture.write_text(
            'from pathlib import Path\nimport unittest\n'
            'class Cases(unittest.TestCase):\n'
            '    def record(self, name):\n'
            '        marker = Path(__file__).with_name(name)\n'
            '        with marker.open("x", encoding="utf-8") as output:\n'
            '            output.write("검증")\n'
            '    def test_first(self): self.record("first")\n'
            '    def test_second(self): self.record("second")\n'
            '    def test_third(self): self.record("third")\n',
            encoding='utf-8',
        )
        for workers in (1, 2):
            with self.subTest(workers=workers):
                result = self.run_fixture(workers)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn('Common tests: ran 3/3', result.stderr)
                for name in ('first', 'second', 'third'):
                    marker = self.directory / name
                    self.assertEqual(marker.read_text(encoding='utf-8'), '검증')
                    marker.unlink()

    def test_failure_details_and_skips_survive_worker_aggregation(self):
        (self.directory / 'test_fixture.py').write_text(
            'import unittest\nclass Cases(unittest.TestCase):\n'
            '    def test_failure(self): self.fail("specific failure")\n'
            '    @unittest.skip("specific skip")\n'
            '    def test_skip(self): pass\n',
            encoding='utf-8',
        )
        result = self.run_fixture()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn('specific failure', result.stderr)
        self.assertIn('Common tests: ran 2/2', result.stderr)
        self.assertIn('failures=1, errors=0, skipped=1', result.stderr)

    def test_empty_or_broken_discovery_fails_before_running_cases(self):
        result = self.run_fixture()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn('found no tests', result.stderr)
        (self.directory / 'test_fixture.py').write_text(
            'this is invalid python!\n', encoding='utf-8',
        )
        result = self.run_fixture()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn('Common test discovery failed', result.stderr)
        self.assertIn('SyntaxError', result.stderr)
        self.assertNotIn('Worker 1/', result.stderr)

    def test_worker_crash_returns_failure_instead_of_waiting_for_lost_results(self):
        (self.directory / 'test_fixture.py').write_text(
            'import os\nimport unittest\nclass Cases(unittest.TestCase):\n'
            '    @classmethod\n'
            '    def setUpClass(cls): os._exit(7)\n'
            '    def test_crash(self): pass\n',
            encoding='utf-8',
        )
        result = self.run_fixture()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn('Common test worker failed', result.stderr)


if __name__ == '__main__':
    unittest.main()
