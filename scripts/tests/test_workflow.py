import contextlib
import io
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SKILL_SCRIPTS = Path(__file__).parents[1] / 'skills'
sys.path.insert(0, str(SKILL_SCRIPTS))
spec = importlib.util.spec_from_file_location('workflow', SKILL_SCRIPTS / 'workflow.py')
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)


class WorkflowTests(unittest.TestCase):
    def setUp(self):
        remote_patch = patch.object(
            w,
            'main_remote',
            return_value='origin',
        )
        remote_patch.start()
        self.addCleanup(remote_patch.stop)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.remote = self.root / 'origin.git'
        self.primary = self.root / 'main'
        self.other = self.root / 'other'
        self.command(self.root, 'git', 'init', '--bare', str(self.remote))
        self.command(self.root, 'git', 'clone', str(self.remote), str(self.primary))
        self.configure(self.primary)
        w.git(self.primary, 'checkout', '-b', 'main')
        (self.primary / 'README.md').write_text('initial\n')
        self.commit(self.primary, 'initial')
        w.git(self.primary, 'push', '-u', 'origin', 'main')
        w.git(self.remote, 'symbolic-ref', 'HEAD', 'refs/heads/main')
        self.command(self.root, 'git', 'clone', str(self.remote), str(self.other))
        self.configure(self.other)

    def command(self, root, *args):
        return w.run(root, *args)

    def test_subprocess_output_decodes_utf8_and_legacy_locale(self):
        self.assertEqual(w.decode_output('기여자'.encode('utf-8')), '기여자')
        with patch.object(w.locale, 'getencoding', return_value='cp949'):
            self.assertEqual(w.decode_output('검증'.encode('cp949')), '검증')
        self.assertEqual(w.decode_output(b'first\r\nsecond\rthird'), 'first\nsecond\nthird')

    def configure(self, root):
        w.git(root, 'config', 'user.email', 'workflow@example.test')
        w.git(root, 'config', 'user.name', 'Workflow test')
        w.git(root, 'config', 'commit.gpgsign', 'false')

    def commit(self, root, message):
        w.git(root, 'add', '.')
        w.git(root, 'commit', '-m', message)

    def start(self, name='task', platform='shared'):
        path = self.root / name
        with contextlib.redirect_stdout(io.StringIO()):
            w.main(['--repo', str(self.primary), 'start', name, '--platform', platform, '--worktree', str(path)])
        return path

    def merged_native_worktree(self, platform='windows'):
        path = self.start('old', platform=platform)
        (path / 'old.txt').write_text('completed work\n')
        self.commit(path, 'completed work')
        checked_head = w.head(path)
        w.git(self.primary, 'merge', '--squash', f'{platform}/old')
        self.commit(self.primary, 'squashed completed work')
        merge = w.head(self.primary)
        w.git(self.primary, 'push', 'origin', 'main')
        task = w.read_state(path)['old']
        task.update(
            status='complete', merge=merge, pr='1',
            checked={'head': checked_head, 'time': 0},
        )
        w.update_task(path, 'old', task)
        current = self.start('current', platform=platform)
        return path, current, task, merge

    def cleanup_old(self, current, merge, now, *, open_prs=None):
        with (
            patch.object(w, 'source_repository', return_value='sidey-app/SIDEY-source'),
            patch.object(w, 'open_task_prs', return_value=open_prs or []),
        ):
            return w.cleanup_merged_worktrees(
                current, merge, now=now,
            )

    def test_cleanup_removes_only_old_verified_native_worktree(self):
        old, current, task, merge = self.merged_native_worktree()
        committed_at = int(w.git(old, 'show', '-s', '--format=%ct', 'HEAD'))
        removed = self.cleanup_old(
            current, merge, committed_at + 3 * 24 * 60 * 60,
        )
        self.assertEqual(removed, [{'task': 'old', 'worktree': str(old.resolve())}])
        self.assertFalse(old.exists())
        self.assertTrue(current.exists())
        self.assertEqual(
            self.command(self.primary, 'git', 'branch', '--list', task['branch']),
            '',
        )

    def assert_cleanup_preserves(self, reason):
        old, current, task, merge = self.merged_native_worktree()
        committed_at = int(w.git(old, 'show', '-s', '--format=%ct', 'HEAD'))
        now = committed_at + 3 * 24 * 60 * 60
        if reason == 'recent':
            task['checked']['time'] = now - 60 * 60
            w.update_task(old, 'old', task)
        elif reason == 'dirty':
            (old / 'untracked.txt').write_text('keep this')
        elif reason == 'new commit':
            (old / 'new.txt').write_text('new work')
            self.commit(old, 'new work')
        open_prs = [{'number': 2}] if reason == 'open PR' else []
        removed = self.cleanup_old(
            current, merge, now, open_prs=open_prs,
        )
        self.assertEqual(removed, [])
        self.assertTrue(old.exists())

    def test_cleanup_preserves_recent_worktree(self):
        self.assert_cleanup_preserves('recent')

    def test_cleanup_preserves_open_pr_worktree(self):
        self.assert_cleanup_preserves('open PR')

    def test_cleanup_preserves_dirty_worktree(self):
        self.assert_cleanup_preserves('dirty')

    def test_cleanup_preserves_worktree_with_new_commit(self):
        self.assert_cleanup_preserves('new commit')

    def test_cleanup_preserves_locked_worktree(self):
        old, current, _, merge = self.merged_native_worktree()
        committed_at = int(w.git(old, 'show', '-s', '--format=%ct', 'HEAD'))
        w.git(self.primary, 'worktree', 'lock', str(old))
        self.assertEqual(self.cleanup_old(
            current, merge, committed_at + 3 * 24 * 60 * 60,
        ), [])
        self.assertTrue(old.exists())

    def test_cleanup_removes_old_completed_shared_worktree(self):
        old, current, task, merge = self.merged_native_worktree('shared')
        committed_at = int(w.git(old, 'show', '-s', '--format=%ct', 'HEAD'))
        self.assertEqual(self.cleanup_old(
            current, merge, committed_at + 3 * 24 * 60 * 60,
        ), [{'task': 'old', 'worktree': str(old.resolve())}])
        self.assertFalse(old.exists())
        self.assertEqual(w.git(self.primary, 'branch', '--list', task['branch']), '')

    def test_cleanup_removes_old_detached_worktree(self):
        old, current, task, merge = self.merged_native_worktree('shared')
        committed_at = int(w.git(old, 'show', '-s', '--format=%ct', 'HEAD'))
        w.git(old, 'switch', '--detach', task['checked']['head'])
        w.git(self.primary, 'branch', '-D', task['branch'])
        self.assertEqual(self.cleanup_old(
            current, merge, committed_at + 3 * 24 * 60 * 60,
        ), [{'task': 'old', 'worktree': str(old.resolve())}])
        self.assertFalse(old.exists())

    def published_merged_task(self):
        old, _, task, merge = self.merged_native_worktree('shared')
        w.git(old, 'push', '-u', 'origin', task['branch'])
        task['published'] = {
            'head_ref': f"sidey-app:{task['branch']}",
            'push_remote': 'origin',
        }
        w.update_task(old, 'old', task)
        return old, task, merge

    def cleanup_pr_branch(self, old, task, merge, *, open_prs=None):
        real_run = w.run

        def response(root, *args, **kwargs):
            if args[:3] == ('gh', 'pr', 'view'):
                return json.dumps({
                    'state': 'MERGED',
                    'headRefOid': task['checked']['head'],
                    'mergeCommit': {'oid': merge},
                })
            return real_run(root, *args, **kwargs)

        with (
            patch.object(w, 'source_repository', return_value='sidey-app/SIDEY-source'),
            patch.object(w, 'github_repository_from_remote', return_value='sidey-app/SIDEY-source'),
            patch.object(w, 'open_task_prs', return_value=open_prs or []),
            patch.object(w, 'run', side_effect=response),
        ):
            return w.cleanup_merged_pr_branch(old, task, merge)

    def test_merged_pr_branch_deleted_from_remote_and_local_after_main_update(self):
        old, task, merge = self.published_merged_task()
        self.assertEqual(self.cleanup_pr_branch(old, task, merge), {
            'remote_deleted': True, 'local_deleted': True,
        })
        self.assertEqual(w.git(old, 'ls-remote', '--heads', 'origin',
                               f"refs/heads/{task['branch']}"), '')
        self.assertEqual(w.git(old, 'branch', '--list', task['branch']), '')
        self.assertEqual(w.git(old, 'branch', '--show-current'), '')
        self.assertTrue(old.exists())
        self.assertEqual(w.owned_task(old, 'old')['status'], 'complete')
        self.assertEqual(self.cleanup_pr_branch(old, task, merge), {
            'remote_deleted': False, 'local_deleted': False,
        })

    def test_merged_pr_branch_kept_when_remote_head_changed(self):
        old, task, merge = self.published_merged_task()
        w.git(self.other, 'fetch', 'origin', task['branch'])
        w.git(self.other, 'checkout', '-b', task['branch'], 'FETCH_HEAD')
        (self.other / 'later.txt').write_text('later work\n')
        self.commit(self.other, 'later work')
        w.git(self.other, 'push', 'origin', task['branch'])
        with self.assertRaisesRegex(w.WorkflowError, 'remote head changed'):
            self.cleanup_pr_branch(old, task, merge)
        self.assertTrue(w.git(old, 'ls-remote', '--heads', 'origin',
                              f"refs/heads/{task['branch']}"))
        self.assertEqual(w.git(old, 'branch', '--show-current'), task['branch'])

    def test_merged_pr_branch_kept_while_another_pr_is_open(self):
        old, task, merge = self.published_merged_task()
        with self.assertRaisesRegex(w.WorkflowError, 'still in use'):
            self.cleanup_pr_branch(old, task, merge, open_prs=[{'number': 99}])
        self.assertTrue(w.git(old, 'ls-remote', '--heads', 'origin',
                              f"refs/heads/{task['branch']}"))
        self.assertEqual(w.git(old, 'branch', '--show-current'), task['branch'])

    def test_merged_pr_branch_kept_when_checked_out_elsewhere(self):
        old, task, merge = self.published_merged_task()
        w.git(old, 'switch', '--detach', task['checked']['head'])
        another = self.root / 'another-worktree'
        w.git(self.primary, 'worktree', 'add', str(another), task['branch'])
        with self.assertRaisesRegex(w.WorkflowError, 'checked out elsewhere'):
            self.cleanup_pr_branch(old, task, merge)
        self.assertTrue(w.git(old, 'ls-remote', '--heads', 'origin',
                              f"refs/heads/{task['branch']}"))

    def test_finish_retries_cleanup_for_completed_native_task(self):
        old, current, _, merge = self.merged_native_worktree()
        task = w.read_state(current)['current']
        task.update(
            status='complete', merge=merge,
            checked={'head': w.head(current)},
        )
        w.update_task(current, 'current', task)
        with (
            patch.object(w, 'source_repository', return_value='sidey-app/SIDEY-source'),
            patch.object(w, 'cleanup_merged_pr_branch', return_value={
                'remote_deleted': False, 'local_deleted': False,
            }),
            patch.object(w, 'cleanup_merged_worktrees', return_value=[str(old)]) as cleanup,
            contextlib.redirect_stdout(io.StringIO()) as output,
        ):
            w.main(['--repo', str(current), 'finish', 'current'])
        result = json.loads(output.getvalue())
        self.assertEqual(result['status'], 'complete')
        self.assertEqual(result['removed_worktrees'], [str(old)])
        cleanup.assert_called_once_with(current.resolve(), merge)

    def test_windows_test_installer_replaces_old_setup_after_verified_build(self):
        downloads = self.root / 'Downloads'
        downloads.mkdir()
        old = downloads / 'SIDEY-Windows-x64-v1.0.0-Setup.exe'
        old.write_bytes(b'old installer')
        same_version = downloads / 'SIDEY-Windows-x64-v1.0.1-Setup.exe'
        same_version.write_bytes(b'previous build')
        unrelated = downloads / 'Other-Setup.exe'
        unrelated.write_bytes(b'keep')

        def build(_primary, output):
            installer = output / 'artifacts' / 'SIDEY-Windows-x64-v1.0.1-Setup.exe'
            installer.parent.mkdir()
            installer.write_bytes(b'verified new installer')
            return installer

        remote = w.head(self.primary)
        with patch.object(w, 'build_windows_test_installer', side_effect=build):
            result = w.publish_windows_test_installer(
                self.primary, remote, downloads,
            )
        self.assertEqual(
            Path(result['path']).read_bytes(), b'verified new installer',
        )
        self.assertEqual(result['removed_installers'], [str(old.resolve())])
        self.assertFalse(old.exists())
        self.assertEqual(same_version.read_bytes(), b'verified new installer')
        self.assertEqual(unrelated.read_bytes(), b'keep')
        self.assertEqual(result['main'], remote)

    def test_windows_test_installer_preserves_old_setup_when_main_changes(self):
        downloads = self.root / 'Downloads'
        downloads.mkdir()
        old = downloads / 'SIDEY-Windows-x64-v1.0.0-Setup.exe'
        old.write_bytes(b'old installer')

        def build(_primary, output):
            installer = output / 'artifacts' / 'SIDEY-Windows-x64-v1.0.1-Setup.exe'
            installer.parent.mkdir()
            installer.write_bytes(b'stale installer')
            self.advance()
            return installer

        remote = w.head(self.primary)
        with patch.object(w, 'build_windows_test_installer', side_effect=build):
            with self.assertRaisesRegex(w.WorkflowError, 'main changed'):
                w.publish_windows_test_installer(
                    self.primary, remote, downloads,
                )
        self.assertEqual(old.read_bytes(), b'old installer')
        self.assertFalse(
            (downloads / 'SIDEY-Windows-x64-v1.0.1-Setup.exe').exists(),
        )

    def test_completed_windows_finish_includes_test_installer(self):
        _, current, _, merge = self.merged_native_worktree()
        task = w.read_state(current)['current']
        task.update(
            status='complete', merge=merge,
            checked={'head': w.head(current)},
            app_review={'main': merge},
        )
        w.update_task(current, 'current', task)
        candidate = {'path': 'Downloads/SIDEY-Setup.exe', 'main': merge}
        with (
            patch.object(w, 'source_repository', return_value='sidey-app/SIDEY-source'),
            patch.object(w, 'cleanup_merged_pr_branch', return_value={
                'remote_deleted': False, 'local_deleted': False,
            }),
            patch.object(w, 'cleanup_merged_worktrees', return_value=[]),
            patch.object(w, 'publish_windows_test_installer', return_value=candidate) as publish,
            contextlib.redirect_stdout(io.StringIO()) as output,
        ):
            w.main(['--repo', str(current), 'finish', 'current'])
        self.assertEqual(json.loads(output.getvalue())['test_installer'], candidate)
        publish.assert_called_once_with(self.primary.resolve(), merge)

    def test_checked_windows_task_builds_test_installer_before_merge(self):
        path = self.start(platform='windows')
        (path / 'windows').mkdir()
        (path / 'windows/work.txt').write_text('implementation\n')
        self.commit(path, 'Windows implementation')
        with patch.object(w, 'local_checks'):
            task = w.check_task(path, 'task')
        downloads = self.root / 'Downloads'
        downloads.mkdir()
        old = downloads / 'SIDEY-Windows-x64-v1.0.0-Setup.exe'
        old.write_bytes(b'old')

        def build(_source, output):
            installer = output / 'artifacts' / 'SIDEY-Windows-x64-v1.0.1-Setup.exe'
            installer.parent.mkdir()
            installer.write_bytes(b'checked branch installer')
            return installer

        with (
            patch.object(w, 'windows_downloads_folder', return_value=downloads),
            patch.object(w, 'build_windows_test_installer', side_effect=build),
        ):
            result = w.test_installer_task(path, 'task')
        self.assertEqual(Path(result['path']).read_bytes(), b'checked branch installer')
        self.assertEqual(result['head'], task['checked']['head'])
        self.assertEqual(result['main'], task['checked']['base'])
        self.assertFalse(old.exists())

    def test_recovers_server_squash_after_lost_client_response_only_for_checked_tree(self):
        path = self.start()
        (path / 'change.md').write_text('task')
        self.commit(path, 'task change')
        task = w.read_state(path)['task']
        task['checked'] = {'head': w.head(path), 'snapshot': w.snapshot(path), 'base': task['base']}
        task['merge_intent'] = {
            'head': w.head(path), 'base': task['base'], 'subject': 'squash task',
            'body_sha256': w.hashlib.sha256(b'').hexdigest(), 'coauthors': [],
        }
        w.git(self.primary, 'merge', '--squash', 'shared/task')
        self.commit(self.primary, 'squash task')
        remote = w.head(self.primary)
        pr = dict(number=42, headRefOid=w.head(path), mergeCommit={'oid': remote},
                  isCrossRepository=False, body='')
        real_run = w.run
        def response(root, *args, **kwargs):
            return json.dumps([pr]) if args[0] == 'gh' else real_run(root, *args, **kwargs)
        with patch.object(w, 'run', side_effect=response):
            recovered = w.recover_merged_task(path, task, remote)
            self.assertEqual(recovered['pr'], '42')
            self.assertEqual(recovered['status'], 'integrated')
            (path / 'change.md').write_text('unchecked change')
            self.assertIsNone(w.recover_merged_task(path, task, remote))
            (path / 'change.md').write_text('task')
            pr['headRefOid'] = 'unrelated'
            self.assertIsNone(w.recover_merged_task(path, task, remote))

    def test_recovery_rejects_a_merge_with_different_content(self):
        path = self.start()
        (path / 'change.md').write_text('task')
        self.commit(path, 'task change')
        task = w.read_state(path)['task']
        task['checked'] = {'head': w.head(path), 'snapshot': w.snapshot(path), 'base': task['base']}
        task['merge_intent'] = {
            'head': w.head(path), 'base': task['base'], 'subject': 'expected subject',
            'body_sha256': w.hashlib.sha256(b'').hexdigest(), 'coauthors': [],
        }
        (self.primary / 'other.md').write_text('other')
        self.commit(self.primary, 'unrelated merge result')
        remote = w.head(self.primary)
        pr = dict(number=42, headRefOid=w.head(path), mergeCommit={'oid': remote},
                  isCrossRepository=False, body='')
        real_run = w.run
        def response(root, *args, **kwargs):
            return json.dumps([pr]) if args[0] == 'gh' else real_run(root, *args, **kwargs)
        with patch.object(w, 'run', side_effect=response):
            self.assertIsNone(w.recover_merged_task(path, task, remote))

    def squashed_macos_task(self):
        opener = self.primary / 'scripts/macos/open_current.sh'
        opener.parent.mkdir(parents=True)
        opener.write_text('#!/bin/sh\nexit 0\n')
        self.commit(self.primary, 'opener fixture')
        w.git(self.primary, 'push', 'origin', 'main')
        path = self.start(platform='macos')
        (path / 'macos').mkdir()
        (path / 'macos/source.swift').write_text('reviewed native change\n')
        self.commit(path, 'native task change')
        task = w.read_state(path)['task']
        task['checked'] = {'head': w.head(path), 'snapshot': w.snapshot(path), 'base': task['base']}
        w.git(self.primary, 'merge', '--squash', 'macos/task')
        self.commit(self.primary, 'squashed native change')
        task.update(status='main-updated', merge=w.head(self.primary))
        w.git(self.primary, 'push', 'origin', 'main')
        w.update_task(path, 'task', task)
        self.assertFalse(w.is_ancestor(path, task['checked']['head'], task['merge']))
        self.assertTrue(w.same_tree(path, task['checked']['head'], task['merge']))
        return path, task

    def test_open_accepts_squashed_checked_tree_after_main_advances(self):
        path, task = self.squashed_macos_task()
        (self.primary / 'later.md').write_text('subsequent reviewed change\n')
        self.commit(self.primary, 'advance after squash')
        w.git(self.primary, 'push', 'origin', 'main')
        remote = w.head(self.primary)
        self.assertFalse(w.same_tree(path, task['merge'], remote))
        real_run = w.run
        opened = []
        def response(root, *args, **kwargs):
            if Path(args[0]) == self.primary.resolve() / 'scripts/macos/open_current.sh':
                opened.append((root, args))
                return ''
            return real_run(root, *args, **kwargs)
        with patch.object(w, 'run', side_effect=response), contextlib.redirect_stdout(io.StringIO()):
            w.main(['--repo', str(path), 'open', '--task', 'task'])
        primary = self.primary.resolve()
        self.assertEqual(opened, [(primary, (str(primary / 'scripts/macos/open_current.sh'),
            '--worktree', str(primary), '--scheme', 'SIDEY'))])
        completed = w.read_state(path)['task']
        self.assertEqual(completed['status'], 'complete')
        self.assertEqual(completed['app_review']['main'], remote)
        self.assertEqual(completed['app_review']['source'], str(primary))

    def test_open_rejects_unverified_squash_and_wrong_app_without_launching(self):
        path, task = self.squashed_macos_task()
        invalid = [
            ('not integrated', {**task, 'status': 'started'}),
            ('wrong platform', {**task, 'platform': 'windows'}),
            ('wrong app', {**task, 'app': 'sidey-reals'}),
            ('missing merge', {key: value for key, value in task.items() if key != 'merge'}),
            ('missing check', {key: value for key, value in task.items() if key != 'checked'}),
            ('missing checked head', {**task, 'checked': {}}),
            ('merge outside main', {**task, 'merge': task['checked']['head']}),
            ('different tree', {**task, 'merge': task['base']}),
            ('missing task', None),
        ]
        real_run = w.run
        def response(root, *args, **kwargs):
            if Path(args[0]) == self.primary.resolve() / 'scripts/macos/open_current.sh':
                self.fail('Unverified task must not launch the app')
            return real_run(root, *args, **kwargs)
        for reason, state in invalid:
            with self.subTest(reason=reason):
                w.update_task(path, 'task', state)
                with patch.object(w, 'run', side_effect=response), self.assertRaisesRegex(
                        w.WorkflowError, 'integrated and awaiting'):
                    w.main(['--repo', str(path), 'open', '--task', 'task'])
                self.assertEqual(w.read_state(path)['task'], state)

    def test_open_accepts_legacy_app_name_in_existing_task_state(self):
        path, task = self.squashed_macos_task()
        task['app'] = 'SIDEYAppStore'
        w.update_task(path, 'task', task)
        real_run = w.run
        opened = []

        def response(root, *args, **kwargs):
            if (
                Path(args[0])
                == self.primary.resolve() / 'scripts/macos/open_current.sh'
            ):
                opened.append(args)
                return ''
            return real_run(root, *args, **kwargs)

        with (
            patch.object(w, 'run', side_effect=response),
            contextlib.redirect_stdout(io.StringIO()),
        ):
            w.main(
                ['--repo', str(path), 'open', '--task', 'task']
            )

        self.assertEqual(len(opened), 1)
        self.assertEqual(w.read_state(path)['task']['status'], 'complete')

    def test_merge_uses_github_squash_defaults_without_message_overrides(self):
        with patch.object(w, 'run') as run:
            w.merge_task_pr(self.primary, '108', 'checked-head')
        command = run.call_args.args[1:]
        self.assertEqual(
            command,
            (
                'gh', 'pr', 'merge', '108', '--squash',
                '--match-head-commit', 'checked-head',
            ),
        )
        self.assertNotIn('--subject', command)
        self.assertNotIn('--body-file', command)

    def test_publish_preserves_existing_pr_body_without_editing_or_merging(self):
        template = self.write_general_pr_template()
        body = template.read_text(encoding='utf-8')
        task = {
            'checked': {'base': 'base', 'head': 'checked-head'},
            'status': 'checked',
        }
        old_pr = {
            'number': 42,
            'headRefOid': 'old-head',
            'isCrossRepository': False,
        }
        current_pr = {**old_pr, 'headRefOid': 'checked-head'}
        views = iter((
            {'title': 'fix(auth): 인증 오류 수정', 'body': body},
            {'title': 'fix(auth): 인증 오류 수정', 'body': body},
        ))
        commands = []

        def response(root, *args, **kwargs):
            commands.append(args)
            if args[:3] == ('gh', 'pr', 'view'):
                return json.dumps(next(views))
            return ''

        args = w.argparse.Namespace(task='task', title=None, body_file=None)
        with (
            patch.object(w, 'owned_task', return_value=task),
            patch.object(w, 'dirty_paths', return_value=[]),
            patch.object(
                w,
                'source_repository',
                return_value='sidey-app/SIDEY-source',
            ),
            patch.object(w, 'fetch_main', return_value='base'),
            patch.object(w, 'attest'),
            patch.object(w, 'changed_paths', return_value=['docs/guide.md']),
            patch.object(w, 'validate_paths'),
            patch.object(
                w,
                'publish_head',
                return_value='sidey-app:shared/task',
            ),
            patch.object(w, 'open_task_prs', side_effect=([old_pr], [current_pr])),
            patch.object(w, 'branch', return_value='shared/task'),
            patch.object(w, 'head', return_value='checked-head'),
            patch.object(w, 'git') as git,
            patch.object(w, 'update_task') as update_task,
            patch.object(w, 'run', side_effect=response),
        ):
            result = w.publish(self.primary, args)

        git.assert_called_once_with(
            self.primary,
            'push',
            '-u',
            'origin',
            'shared/task',
        )
        self.assertFalse(any(command[:3] == ('gh', 'pr', 'edit') for command in commands))
        self.assertFalse(any(command[:3] == ('gh', 'pr', 'merge') for command in commands))
        self.assertFalse(any(command[:3] == ('gh', 'pr', 'create') for command in commands))
        self.assertEqual(result, {
            'status': 'published',
            'pr': '42',
            'head': 'checked-head',
        })
        self.assertEqual(update_task.call_args.args[2]['status'], 'published')

    def test_publish_creates_pr_with_exact_validated_body_file(self):
        template = self.write_general_pr_template()
        body = template.read_text(encoding='utf-8')
        task = {
            'checked': {'base': 'base', 'head': 'checked-head'},
            'status': 'checked',
        }
        current_pr = {
            'number': 42,
            'headRefOid': 'checked-head',
            'isCrossRepository': False,
        }
        commands = []

        def response(root, *args, **kwargs):
            commands.append(args)
            if args[:3] == ('gh', 'pr', 'view'):
                return json.dumps({
                    'title': 'fix(auth): 인증 오류 수정',
                    'body': body,
                })
            return ''

        args = w.argparse.Namespace(
            task='task',
            title='fix(auth): 인증 오류 수정',
            body_file=str(template),
        )
        with (
            patch.object(w, 'owned_task', return_value=task),
            patch.object(w, 'dirty_paths', return_value=[]),
            patch.object(
                w,
                'source_repository',
                return_value='sidey-app/SIDEY-source',
            ),
            patch.object(w, 'fetch_main', return_value='base'),
            patch.object(w, 'attest'),
            patch.object(w, 'changed_paths', return_value=['docs/guide.md']),
            patch.object(w, 'validate_paths'),
            patch.object(
                w,
                'publish_head',
                return_value='sidey-app:shared/task',
            ),
            patch.object(w, 'open_task_prs', side_effect=([], [current_pr])),
            patch.object(w, 'branch', return_value='shared/task'),
            patch.object(w, 'head', return_value='checked-head'),
            patch.object(w, 'git'),
            patch.object(w, 'update_task'),
            patch.object(w, 'run', side_effect=response),
        ):
            w.publish(self.primary, args)

        create = next(command for command in commands if command[:3] == ('gh', 'pr', 'create'))
        body_file = Path(create[create.index('--body-file') + 1])
        self.assertEqual(body_file, template.resolve())
        self.assertEqual(body_file.read_text(encoding='utf-8'), body)
        self.assertNotIn('Co-authored-by:', body)
        self.assertFalse(any(command[:3] == ('gh', 'pr', 'edit') for command in commands))

    def advance(self):
        (self.other / 'advance.md').write_text('remote update\n')
        self.commit(self.other, 'advance main')
        w.git(self.other, 'push', 'origin', 'main')
        return w.head(self.other)

    def test_start_fetches_real_remote_not_stale_tracking_ref(self):
        old = w.git(self.primary, 'rev-parse', 'origin/main')
        new = self.advance()
        self.assertNotEqual(old, new)
        task = self.start()
        self.assertEqual(w.head(task), new)

    def test_start_preserves_another_dirty_worktree(self):
        (self.primary / 'README.md').write_text('in progress\n')
        (self.primary / 'untracked.swift').write_text('user source\n')
        self.start()
        self.assertEqual((self.primary / 'README.md').read_text(), 'in progress\n')
        self.assertTrue((self.primary / 'untracked.swift').exists())

    def test_start_refuses_existing_directory_and_duplicate_owner(self):
        task = self.start()
        with self.assertRaises(w.WorkflowError):
            w.main(['--repo', str(self.primary), 'start', 'task', '--platform', 'shared', '--worktree', str(task)])

    def test_task_state_round_trips_korean_as_utf8(self):
        task = self.start()
        state = w.read_state(task)['task']
        state['merge_intent'] = {'subject': '한글 squash 제목'}
        w.update_task(task, 'task', state)
        state_path = w.common_dir(task) / 'sidey-workflow' / 'tasks.json'
        self.assertIn('한글 squash 제목', state_path.read_bytes().decode('utf-8'))
        self.assertEqual(w.read_state(task)['task']['merge_intent']['subject'], '한글 squash 제목')

    def test_legacy_locale_task_state_is_migrated_to_utf8(self):
        task = self.start()
        state_path = w.common_dir(task) / 'sidey-workflow' / 'tasks.json'
        state = w.read_state(task)
        state['task']['merge_intent'] = {'subject': '예전 한글 제목'}
        state_path.write_bytes(json.dumps(state, ensure_ascii=False).encode('cp949'))
        with patch.object(w.locale, 'getencoding', return_value='cp949'):
            self.assertEqual(w.read_state(task)['task']['merge_intent']['subject'], '예전 한글 제목')
        self.assertIn('예전 한글 제목', state_path.read_bytes().decode('utf-8'))

    def test_moved_file_checks_deleted_platform_path(self):
        (self.primary / 'macos').mkdir()
        (self.primary / 'macos/source.swift').write_text('native\n')
        self.commit(self.primary, 'fixture')
        w.git(self.primary, 'push')
        task = self.start()
        w.git(task, 'mv', 'macos/source.swift', 'source.swift')
        paths = w.changed_paths(task, 'origin/main', dirty=True)
        self.assertIn('macos/source.swift', paths)
        with self.assertRaises(w.WorkflowError):
            w.validate_paths('shared/task', paths)

    def test_untracked_platform_file_cannot_escape_guard(self):
        task = self.start()
        (task / 'windows').mkdir()
        (task / 'windows/new.cs').write_text('source\n')
        with self.assertRaises(w.WorkflowError):
            w.validate_paths('shared/task', w.changed_paths(task, 'origin/main', dirty=True))

    def test_main_cannot_implement(self):
        with self.assertRaises(w.WorkflowError):
            w.validate_paths('main', ['README.md'])

    def test_platform_branches_reject_shared_changes(self):
        for name in ('macos/task', 'windows/task'):
            with self.assertRaises(w.WorkflowError):
                w.validate_paths(name, ['docs/architecture.md'])

    def test_platform_release_manifests_follow_their_native_branches(self):
        self.assertEqual(w.platform_for('release/macos.json'), 'macos')
        self.assertEqual(w.platform_for('release/windows.json'), 'windows')
        self.assertEqual(
            w.validate_paths('macos/release', ['release/macos.json']),
            'macos',
        )
        self.assertEqual(
            w.validate_paths('windows/release', ['release/windows.json']),
            'windows',
        )

    def test_shared_commits_already_in_main_are_excluded(self):
        task = self.start(platform='macos')
        (task / 'macos').mkdir()
        (task / 'macos/source.swift').write_text('native\n')
        self.commit(task, 'native')
        new = self.advance()
        w.fetch_main(task)
        w.git(task, 'merge', '--no-edit', new)
        self.assertEqual(w.changed_paths(task, new), ['macos/source.swift'])

    def test_check_invalidates_when_main_advances_during_checks(self):
        task = self.start()
        with patch.object(w, 'local_checks', side_effect=lambda *_: self.advance()):
            with self.assertRaisesRegex(w.WorkflowError, 'changed during'):
                w.check_task(task, 'task')
        self.assertNotIn('checked', w.owned_task(task, 'task'))

    def test_check_invalidates_when_source_changes_during_checks(self):
        task = self.start()
        with patch.object(w, 'local_checks', side_effect=lambda *_: (task / 'new.txt').write_text('new')):
            with self.assertRaisesRegex(w.WorkflowError, 'changed during'):
                w.check_task(task, 'task')

    def test_attestation_rejects_edit_after_check_and_staging(self):
        task = self.start()
        (task / 'README.md').write_text('task changes')
        with patch.object(w, 'local_checks'):
            state = w.check_task(task, 'task')
        w.attest(task, state, state['base'])
        w.git(task, 'add', 'README.md')
        with self.assertRaises(w.WorkflowError):
            w.attest(task, state, state['base'])

    def test_sync_refuses_dirty_task_and_other_owner(self):
        task = self.start()
        (task / 'README.md').write_text('unfinished')
        with self.assertRaises(w.WorkflowError):
            w.main(['--repo', str(task), 'sync', 'task'])
        with self.assertRaises(w.WorkflowError):
            w.owned_task(self.primary, 'task')

    def test_offline_fetch_does_not_reuse_previous_check(self):
        task = self.start()
        w.git(task, 'remote', 'set-url', 'origin', str(self.root / 'missing.git'))
        with self.assertRaises(w.WorkflowError):
            w.check_task(task, 'task')

    def test_primary_main_update_refuses_dirty_state(self):
        remote = self.advance()
        w.fetch_main(self.primary)
        old = w.head(self.primary)
        (self.primary / 'README.md').write_text('owned by user')
        with self.assertRaisesRegex(w.WorkflowError, 'completion pending'):
            w.update_main(self.primary, remote)
        self.assertEqual(w.head(self.primary), old)
        self.assertEqual((self.primary / 'README.md').read_text(), 'owned by user')

    def test_catalog_runs_public_consumers(self):
        self.assertEqual(set(w.required_scopes(['assets/v1/commerce-catalog.json'])),
                         {'shared', 'macos', 'windows', 'web'})
        self.assertIn('windows', w.required_scopes(['website/src/pages/ko/terms.md']))

    def test_contributor_architecture_only_changes_require_repository_validation(self):
        paths = [
            'AGENTS.md',
            'macos/AGENTS.md',
            'windows/AGENTS.md',
            'windows/docs/AGENTS.md',
            'website/AGENTS.md',
            '.agents/skills/version-audit/SKILL.md',
            '.agents/skills/write-tests/agents/openai.yaml',
            'scripts/skills/validate_contributor_architecture.py',
            'scripts/tests/test_contributor_architecture.py',
            'scripts/skills/commit/validate_commit_message.py',
            'scripts/tests/test_validate_commit_message.py',
            'scripts/skills/commit/prepare_commit_msg.py',
        ]
        self.assertEqual(w.required_scopes(paths), ['shared'])
        self.assertEqual(w.platform_for('macos/AGENTS.md'), 'macos')
        self.assertEqual(w.platform_for('windows/AGENTS.md'), 'windows')
        self.assertEqual(w.platform_for('website/AGENTS.md'), 'shared')
        self.assertEqual(
            w.validate_paths(
                'shared/contributor-architecture',
                [path for path in paths if not path.startswith(('macos/', 'windows/'))],
            ),
            'shared',
        )
        self.assertEqual(w.validate_paths('macos/contributor-docs', ['macos/AGENTS.md']), 'macos')
        self.assertEqual(
            w.validate_paths(
                'windows/contributor-docs',
                ['windows/AGENTS.md', 'windows/docs/AGENTS.md'],
            ),
            'windows',
        )

    def test_contributor_classification_does_not_hide_product_changes(self):
        self.assertEqual(
            w.required_scopes(['AGENTS.md', 'macos/Sources/SIDEY/App.swift']),
            ['macos', 'shared'],
        )
        self.assertEqual(
            w.required_scopes(['website/AGENTS.md', 'website/src/pages/index.astro']),
            ['shared', 'web'],
        )

    def test_commit_text_validation_fails_before_repository_mutation(self):
        w.require_valid_commit_text('Commit message', 'chore(Shared): 기여자 구조 정리')
        w.require_valid_commit_text(
            'PR title', 'chore(Shared): 기여자 구조 정리', subject_only=True
        )
        with self.assertRaisesRegex(w.WorkflowError, 'commit policy'):
            w.require_valid_commit_text('Commit message', 'Contributor architecture cleanup')
        with self.assertRaisesRegex(w.WorkflowError, 'commit policy'):
            w.require_valid_commit_text('PR title', 'Invalid title', subject_only=True)

    def write_general_pr_template(self):
        template = self.primary / '.github/PULL_REQUEST_TEMPLATE/general.md'
        template.parent.mkdir(parents=True, exist_ok=True)
        template.write_text(
            '<!-- SIDEY_GENERAL_PR_TEMPLATE: keep -->\n\n'
            '## PR 유형\n\n- [ ] macOS 구현\n\n'
            '## 변경 내용\n\n설명\n\n'
            '## 검증\n\n검증\n\n'
            '## 확인 사항\n\n- [ ] 확인\n',
            encoding='utf-8',
        )
        return template

    def test_general_pr_body_accepts_filled_template_and_extra_sections(self):
        template = self.write_general_pr_template()
        body = template.read_text(encoding='utf-8').replace('[ ]', '[x]')
        body += '\n## 추가 정보\n\n검토 참고 사항\n'
        self.assertEqual(
            w.require_pr_body(self.primary, body, ['docs/guide.md']),
            'general',
        )

    def test_pr_body_is_not_subject_to_commit_line_length(self):
        template = self.write_general_pr_template()
        body = template.read_text(encoding='utf-8').replace('[ ]', '[x]')
        body += '\n## Additional context\n\n' + ('detail ' * 20) + '\n'

        self.assertGreater(max(map(len, body.splitlines())), 72)
        self.assertEqual(
            w.require_valid_pr(
                self.primary,
                'chore(Shared): 기여자 구조 정리',
                body,
                ['docs/guide.md'],
            ),
            'general',
        )

    def test_custom_pr_body_allows_changed_sections_but_rejects_empty_text(self):
        self.write_general_pr_template()
        self.assertEqual(
            w.require_pr_body(self.primary, '# 캐릭터 에셋 PR\n', ['docs/guide.md']),
            'general',
        )
        marker = w.GENERAL_PR_MARKER
        missing = f'{marker}\n\n## PR 유형\n\n## 검증\n\n## 확인 사항\n'
        self.assertEqual(w.require_pr_body(self.primary, missing, ['docs/guide.md']), 'general')
        reordered = (
            f'{marker}\n\n## 변경 내용\n\n## PR 유형\n\n'
            '## 검증\n\n## 확인 사항\n'
        )
        self.assertEqual(w.require_pr_body(self.primary, reordered, ['docs/guide.md']), 'general')
        with self.assertRaisesRegex(w.WorkflowError, 'description'):
            w.require_pr_body(self.primary, marker, ['docs/guide.md'])

    def test_general_pr_body_file_requires_existing_utf8_file(self):
        self.write_general_pr_template()
        with self.assertRaisesRegex(w.WorkflowError, 'Cannot read --body-file'):
            w.require_pr_body_file(
                self.primary,
                'missing.md',
                ['docs/guide.md'],
            )
        invalid = self.primary / 'invalid.md'
        invalid.write_bytes(b'\x80')
        with self.assertRaisesRegex(w.WorkflowError, 'UTF-8'):
            w.require_pr_body_file(
                self.primary,
                invalid,
                ['docs/guide.md'],
            )

    def test_local_python_checks_are_locale_independent(self):
        with patch.object(w, 'run') as run:
            w.local_checks(self.primary, 'shared')
        python_commands = [
            call.args[1:]
            for call in run.call_args_list
            if call.args[1] == w.sys.executable
        ]
        self.assertTrue(python_commands)
        self.assertTrue(all(command[1:3] == ('-X', 'utf8') for command in python_commands))
        self.assertIn(
            (
                w.sys.executable, '-X', 'utf8', '-m', 'unittest', 'discover', '-s',
                'scripts/skills/release-notes/tests',
            ),
            python_commands,
        )

    def test_release_manifests_run_the_matching_native_checks(self):
        self.assertEqual(
            w.required_scopes(['release/version.json']),
            ['macos', 'shared', 'web', 'windows'],
        )
        self.assertEqual(w.required_scopes(['release/macos.json']), ['macos', 'shared'])
        self.assertEqual(w.required_scopes(['release/windows.json']), ['shared', 'windows'])

    def test_workflow_scopes_only_run_affected_platforms(self):
        self.assertEqual(
            w.required_scopes(
                ['.github/workflows/macos-build-and-tests.yml']
            ),
            ['macos', 'shared'],
        )
        self.assertEqual(
            w.required_scopes(['.github/workflows/windows-release.yml']),
            ['shared', 'windows'],
        )
        self.assertEqual(
            w.required_scopes(
                ['.github/workflows/website-deployment.yml']
            ),
            ['shared', 'web'],
        )
        self.assertEqual(w.required_scopes(['scripts/pages/prepare_release_metadata.py']),
                         ['shared', 'web'])

    def test_platform_workflow_only_changes_do_not_require_app_review(self):
        self.assertFalse(
            w.app_review_required(
                'macos',
                ['.github/workflows/macos-build-and-tests.yml'],
            )
        )
        self.assertFalse(
            w.app_review_required(
                'windows',
                ['.github/workflows/windows-build-and-tests.yml'],
            )
        )
        self.assertFalse(w.app_review_required('shared', ['scripts/skills/workflow.py']))

    def test_platform_app_inputs_still_require_app_review(self):
        self.assertTrue(w.app_review_required('macos', ['macos/Sources/SIDEY/App.swift']))
        self.assertTrue(w.app_review_required('windows', ['windows/SIDEY/App.xaml.cs']))
        self.assertTrue(w.app_review_required(
            'windows', ['.github/workflows/windows-release.yml']))
        self.assertTrue(w.app_review_required(
            'macos', [
                '.github/workflows/macos-build-and-tests.yml',
                'scripts/macos/archive_app_store.sh',
            ]))

    def test_ci_workflow_and_scope_logic_run_every_check(self):
        every_scope = {'shared', 'macos', 'windows', 'web'}
        self.assertEqual(set(w.required_scopes(['.github/workflows/ci.yml'])),
                         every_scope)
        self.assertEqual(
            w.required_scopes(['scripts/skills/validate_change.py']),
            ['shared'],
        )
        self.assertEqual(
            set(w.required_scopes(['scripts/skills/validation_scope.py'])),
            every_scope,
        )

    def test_validation_contract_changes_run_all_checks(self):
        self.assertEqual(
            set(w.required_scopes([
                '.github/workflows/ci.yml',
                'scripts/skills/validate_change.py',
            ])),
            {'shared', 'macos', 'windows', 'web'},
        )

    def test_policy_and_contributor_changes_are_repository_only(self):
        self.assertEqual(
            w.required_scopes([
                'scripts/skills/validate_change.py',
                'website/AGENTS.md',
            ]),
            ['shared'],
        )

    def test_windows_docs_stay_owned_without_running_windows_build(self):
        paths = ['windows/docs/debugging.md', 'windows/docs/code-style.md']
        self.assertEqual(w.required_scopes(paths), ['shared'])
        self.assertTrue(all(w.platform_for(path) == 'windows' for path in paths))
        self.assertEqual(w.validate_paths('windows/docs-refresh', paths), 'windows')

    def test_checkout_attributes_require_native_and_web_verification(self):
        self.assertEqual(set(w.required_scopes(['.gitattributes'])),
                         {'shared', 'macos', 'windows', 'web'})


if __name__ == '__main__':
    unittest.main()
