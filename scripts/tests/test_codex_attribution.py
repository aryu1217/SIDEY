"""Exercise real Git commits and hook installation in isolated repositories."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class AttributionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='sidey-attribution-')
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.env = {k: v for k, v in os.environ.items()
                    if not k.startswith(('GIT_', 'CODEX_', 'SIDEY_CODEX_'))}
        self.env.update(GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull)
        self.git('init', '-q')
        self.git('config', 'user.name', 'Human')
        self.git('config', 'user.email', 'human@example.test')
        for relative in (
            'scripts/skills/commit/prepare_commit_msg.py',
            'scripts/skills/commit/pre_push.py',
            'scripts/skills/commit/setup_codex_attribution.py',
        ):
            target = self.repo / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / relative, target)
        self.install()

    def git(self, *args, **kwargs):
        return subprocess.check_output(
            ['git', *args], cwd=self.repo, env=self.env, text=True, encoding='utf-8', **kwargs
        ).strip()

    def install(self, check=True):
        return subprocess.run([
            sys.executable,
            str(self.repo / 'scripts/skills/commit/setup_codex_attribution.py'),
        ],
                              cwd=self.repo, env=self.env, capture_output=True, check=check)

    def commit(self, body='test: change', *args):
        self.git('add', '.')
        self.git('commit', '-q', '--allow-empty', '-m', body, *args)
        return self.git('show', '-s', '--format=%B')

    def test_codex_commit_preserves_human_author(self):
        self.env['CODEX_THREAD_ID'] = 'test-session'
        self.assertIn('Co-authored-by: codex <codex@openai.com>', self.commit())
        self.assertEqual(self.git('show', '-s', '--format=%ae'), 'human@example.test')

    def test_codex_commit_accepts_utf8_korean_message(self):
        self.env['CODEX_THREAD_ID'] = 'test-session'
        message = self.commit('chore(Shared): 기여자 구조 정리')
        self.assertIn('chore(Shared): 기여자 구조 정리', message)
        self.assertIn('Co-authored-by: codex <codex@openai.com>', message)

    def test_human_and_explicit_opt_out(self):
        self.assertNotIn('codex@', self.commit())
        self.env.update(CODEX_THREAD_ID='test', SIDEY_CODEX_COAUTHOR='0')
        self.assertNotIn('codex@', self.commit())

    def test_existing_coauthors_remain_one_block_and_codex_is_not_duplicated(self):
        self.env['CODEX_THREAD_ID'] = 'test'
        original = 'test: change\n\nCo-authored-by: Person <person@example.test>'
        message = self.commit(original)
        self.assertIn('Person <person@example.test>\nCo-authored-by: codex', message)
        message = self.commit(original + '\nCo-authored-by: CODEX <CODEX@OPENAI.COM>')
        self.assertEqual(message.lower().count('codex@openai.com'), 1)

    def test_different_author_and_reused_message_are_preserved(self):
        self.env['CODEX_THREAD_ID'] = 'test'
        self.assertNotIn('codex@', self.commit('test: import', '--author=Original <original@example.test>'))
        self.git('commit', '-q', '--allow-empty', '-C', 'HEAD')
        self.assertNotIn('codex@', self.git('show', '-s', '--format=%B'))

    def test_operation_markers_and_empty_messages_are_not_attributed(self):
        self.env['CODEX_THREAD_ID'] = 'test'
        message = self.repo / 'message.txt'
        hook = self.repo / '.git/hooks/prepare-commit-msg'
        for marker in ('MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REBASE_HEAD'):
            state = self.repo / '.git' / marker
            state.write_text('test')
            message.write_text('Original message\n')
            subprocess.run([sys.executable, str(hook), str(message), 'message'], cwd=self.repo, env=self.env, check=True)
            self.assertEqual(message.read_text(), 'Original message\n')
            state.unlink()
        message.write_text('\n# comment only\n')
        subprocess.run([sys.executable, str(hook), str(message), 'message'], cwd=self.repo, env=self.env, check=True)
        self.assertNotIn('codex@', message.read_text())

    def test_setup_is_idempotent_and_preserves_other_hooks(self):
        sibling = self.repo / '.git/hooks/pre-commit'
        sibling.write_text('existing')
        before = (self.repo / '.git/hooks/prepare-commit-msg').read_bytes()
        pre_push = (self.repo / '.git/hooks/pre-push').read_bytes()
        self.install()
        self.assertEqual(before, (self.repo / '.git/hooks/prepare-commit-msg').read_bytes())
        self.assertEqual(pre_push, (self.repo / '.git/hooks/pre-push').read_bytes())
        self.assertEqual(sibling.read_text(), 'existing')
        (self.repo / '.git/hooks/prepare-commit-msg').write_text('foreign hook')
        self.assertNotEqual(self.install(check=False).returncode, 0)
        self.assertEqual((self.repo / '.git/hooks/prepare-commit-msg').read_text(), 'foreign hook')

    def test_setup_preserves_existing_pre_push_hook_without_partial_install(self):
        destination = self.repo / '.git/hooks/pre-push'
        destination.write_text('foreign hook')
        attribution = self.repo / '.git/hooks/prepare-commit-msg'
        before = attribution.read_bytes()
        self.assertNotEqual(self.install(check=False).returncode, 0)
        self.assertEqual(destination.read_text(), 'foreign hook')
        self.assertEqual(attribution.read_bytes(), before)

    def test_pre_push_blocks_main_but_allows_task_branch(self):
        self.commit()
        remote = self.repo / 'remote.git'
        subprocess.run(['git', 'init', '--bare', '-q', str(remote)],
                       cwd=self.repo, env=self.env, check=True)
        self.git('remote', 'add', 'origin', str(remote))
        self.git('branch', '-M', 'main')
        blocked = subprocess.run(['git', 'push', 'origin', 'HEAD:refs/heads/main'],
                                 cwd=self.repo, env=self.env, capture_output=True, text=True)
        self.assertNotEqual(blocked.returncode, 0)
        self.assertIn('direct pushes to main are blocked', blocked.stderr)
        self.assertEqual(
            self.git('--git-dir', str(remote), 'for-each-ref', '--format=%(refname)', 'refs/heads'),
            '',
        )
        self.git('push', 'origin', 'HEAD:refs/heads/shared/test')
        self.assertEqual(
            self.git('--git-dir', str(remote), 'rev-parse', 'refs/heads/shared/test'),
            self.git('rev-parse', 'HEAD'),
        )

    def test_pre_push_rejects_main_deletion_and_mixed_push(self):
        hook = self.repo / '.git/hooks/pre-push'
        cases = (
            'HEAD abc refs/heads/main def\n',
            '(delete) 000 refs/heads/main def\n',
            'HEAD abc refs/heads/shared/test def\nHEAD abc refs/heads/main def\n',
        )
        for updates in cases:
            with self.subTest(updates=updates):
                result = subprocess.run([sys.executable, str(hook), 'origin', 'url'],
                                        input=updates, cwd=self.repo, env=self.env,
                                        capture_output=True, text=True)
                self.assertEqual(result.returncode, 1)

    def test_setup_preserves_custom_hook_settings(self):
        self.git('config', 'core.hooksPath', 'custom-hooks')
        self.assertNotEqual(self.install(check=False).returncode, 0)
        self.assertEqual(self.git('config', '--get', 'core.hooksPath'), 'custom-hooks')

    def test_setup_accepts_both_line_endings_and_preserves_source_bytes(self):
        source = self.repo / 'scripts/skills/commit/prepare_commit_msg.py'
        destination = self.repo / '.git/hooks/prepare-commit-msg'
        canonical = source.read_bytes().replace(b'\r\n', b'\n')
        for source_ending in (b'\n', b'\r\n'):
            for installed_ending in (b'\n', b'\r\n'):
                with self.subTest(source=source_ending, installed=installed_ending):
                    expected = canonical.replace(b'\n', source_ending)
                    source.write_bytes(expected)
                    destination.write_bytes(canonical.replace(b'\n', installed_ending))
                    self.install()
                    self.assertEqual(destination.read_bytes(), expected)
                    self.install()
                    self.assertEqual(destination.read_bytes(), expected)
                    self.assertEqual(source.read_bytes(), expected)

    def test_linked_worktree_uses_same_installed_hook(self):
        self.commit()
        linked = self.repo / 'linked'
        self.git('worktree', 'add', '-q', '-b', 'linked', str(linked))
        self.repo = linked
        self.env['CODEX_THREAD_ID'] = 'test'
        self.assertIn('codex@openai.com', self.commit())


if __name__ == '__main__':
    unittest.main()
