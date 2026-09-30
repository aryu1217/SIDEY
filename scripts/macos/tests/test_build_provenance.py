import importlib.util
import json
import os
import plistlib
from unittest.mock import patch
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import build_provenance as p
from open_current import verify_running


class ProvenanceTests(unittest.TestCase):
    def test_signed_debug_build_embeds_one_use_launch_proof(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            subprocess.run(['git', 'init', str(root)], check=True, capture_output=True)
            subprocess.run(['git', '-C', str(root), '-c', 'user.name=T', '-c', 'user.email=t@example.test',
                            '-c', 'commit.gpgsign=false', 'commit', '--allow-empty', '-m', 'fixture'],
                           check=True, capture_output=True)
            entitlements = root / 'Config/SIDEY.entitlements'
            entitlements.parent.mkdir()
            entitlements.write_bytes(plistlib.dumps({'com.apple.security.app-sandbox': True}))
            app = root / 'Products/SIDEY.app'
            env = {'TARGET_NAME': 'SIDEY', 'CONFIGURATION': 'Debug',
                   'PRODUCT_BUNDLE_IDENTIFIER': 'app.sidey.desktop.appstore',
                   'DERIVED_FILE_DIR': str(root / 'derived'), 'TARGET_BUILD_DIR': str(app.parent),
                   'UNLOCALIZED_RESOURCES_FOLDER_PATH': 'SIDEY.app/Contents/Resources',
                   'FULL_PRODUCT_NAME': 'SIDEY.app', 'EXECUTABLE_NAME': 'SIDEY',
                   'CODE_SIGN_ENTITLEMENTS': 'Config/SIDEY.entitlements', 'SRCROOT': str(root)}
            original = p.source_state
            with patch.dict(os.environ, env), patch.object(p, 'source_state', side_effect=lambda: original(root)):
                p.begin()
                state = json.loads((root / 'derived/SideyBuildReceipt.json').read_text())
                p.finish()
            receipt = json.loads((app / 'Contents/Resources/SideyBuildReceipt.json').read_text())
            self.assertEqual(receipt['build_id'], state['build_id'])
            self.assertEqual(receipt['executable'], str((app / 'Contents/MacOS/SIDEY').resolve()))
            self.assertTrue(receipt['session'])
            self.assertGreater(receipt['issued_at'], 0)

    def test_xcode_schemes_do_not_use_unreliable_run_preactions(self):
        for name in ('SIDEY', 'SIDEYFeedback', 'SIDEYStoreReview'):
            with self.subTest(scheme=name):
                scheme = ET.parse(p.ROOT / f'macos/SIDEY.xcodeproj/xcshareddata/xcschemes/{name}.xcscheme')
                self.assertIsNone(scheme.find('./LaunchAction/PreActions'))

    def test_ticket_directory_uses_sandbox_for_current_codesign_output(self):
        output = (b'[Dict]\n\t[Key] com.apple.security.app-sandbox\n'
                  b'\t[Value]\n\t\t[Bool] true\n')
        with patch('build_provenance.subprocess.run', return_value=subprocess.CompletedProcess([], 0, output)):
            directory = p.ticket_directory(Path('/tmp/SIDEY.app'), 'app.sidey.desktop.appstore')
        self.assertEqual(directory, Path.home() / 'Library/Containers/app.sidey.desktop.appstore/Data/'
                         'Library/Application Support/SIDEY/BuildReview')

    def test_untracked_source_counts_but_personal_state_and_cache_do_not(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            subprocess.run(['git', 'init', str(root)], check=True, capture_output=True)
            subprocess.run(['git', '-C', str(root), '-c', 'user.name=T', '-c', 'user.email=t@example.test',
                            '-c', 'commit.gpgsign=false', 'commit', '--allow-empty', '-m', 'fixture'], check=True, capture_output=True)
            source = root / 'macos/SIDEY/New.swift'
            source.parent.mkdir(parents=True)
            before = p.source_state(root)['input_hash']
            source.write_text('struct New {}')
            after = p.source_state(root)['input_hash']
            self.assertNotEqual(before, after)
            for path in ['macos/SIDEY/.env', 'macos/SIDEY/.DS_Store', 'macos/SIDEY.xcodeproj/xcuserdata/state',
                         'build/review/output.swift', '_workspace/other/macos/SIDEY/New.swift']:
                file = root / path
                file.parent.mkdir(parents=True, exist_ok=True)
                file.write_text('not a build input')
            self.assertEqual(after, p.source_state(root)['input_hash'])

    def test_running_proof_rejects_same_version_other_build_scheme_and_old_process(self):
        ticket = dict(session='new-session', build_id='new-build', commit='new-commit', input_hash='new-input',
                      target='SIDEY', configuration='Debug')
        receipt = {**ticket, 'executable': '/new/App', 'window_ready': True}
        verify_running(ticket, receipt, '/new/App', '/new/App')
        for key in ticket:
            with self.subTest(key=key), self.assertRaises(RuntimeError):
                verify_running(ticket, {**receipt, key: 'old'}, '/new/App', '/new/App')
        with self.assertRaises(RuntimeError):
            verify_running(ticket, receipt, '/new/App', '/Applications/old/App')
        with self.assertRaises(RuntimeError):
            verify_running(ticket, {**receipt, 'window_ready': False}, '/new/App', '/new/App')

    def test_build_phases_reject_mid_build_changes_and_stamp_unique_private_release(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            subprocess.run(['git', 'init', str(root)], check=True, capture_output=True)
            subprocess.run(['git', '-C', str(root), '-c', 'user.name=T', '-c', 'user.email=t@example.test',
                            '-c', 'commit.gpgsign=false', 'commit', '--allow-empty', '-m', 'fixture'], check=True, capture_output=True)
            source = root / 'macos/SIDEY/New.swift'
            source.parent.mkdir(parents=True)
            source.write_text('struct New {}')
            derived = root / 'derived'
            app = root / 'Products/Test.app'
            env = {'TARGET_NAME': 'SIDEY', 'CONFIGURATION': 'Release',
                   'PRODUCT_BUNDLE_IDENTIFIER': 'app.sidey.test', 'DERIVED_FILE_DIR': str(derived),
                   'TARGET_BUILD_DIR': str(app.parent), 'UNLOCALIZED_RESOURCES_FOLDER_PATH': 'Test.app/Contents/Resources',
                   'SECRET_TOKEN': 'never-include-this', 'SRCROOT': str(root)}
            original = p.source_state
            with patch.dict(os.environ, env), patch.object(p, 'source_state', side_effect=lambda: original(root)):
                p.begin()
                first = json.loads((derived / 'SideyBuildReceipt.json').read_text())
                source.write_text('struct Changed {}')
                with self.assertRaisesRegex(RuntimeError, 'changed during'):
                    p.finish()
                self.assertFalse((app / 'Contents/Resources/SideyBuildReceipt.json').exists())
                p.begin()
                second = json.loads((derived / 'SideyBuildReceipt.json').read_text())
                self.assertNotEqual(first['build_id'], second['build_id'])
                p.finish()
            receipt = (app / 'Contents/Resources/SideyBuildReceipt.json').read_text()
            self.assertNotIn(str(root), receipt)
            self.assertNotIn('never-include-this', receipt)
            (app / 'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleIdentifier': 'app.sidey.test'}))
            p.verify(app, root=root, target='SIDEY', configuration='Release')
            with self.assertRaisesRegex(RuntimeError, 'different target'):
                p.verify(app, root=root, target='OtherTarget')
            source.write_text('struct EditedAfterBuild {}')
            with self.assertRaisesRegex(RuntimeError, 'stale'):
                p.verify(app, root=root)

    def test_source_links_cannot_silently_include_another_worktree(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            directory = root / 'macos/SIDEY'
            directory.mkdir(parents=True)
            (directory / 'Linked').symlink_to(root, target_is_directory=True)
            with self.assertRaisesRegex(RuntimeError, 'Symlink directory'):
                p.inputs(root)

    def test_shared_derived_products_cannot_change_worktree_or_target(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            directory = root / 'products'
            p.claim_build_directory(directory, root, 'SIDEY', 'Debug')
            p.claim_build_directory(directory, root, 'SIDEY', 'Debug')
            for source, target, configuration in [(root / 'other', 'SIDEY', 'Debug'),
                                                  (root, 'OtherTarget', 'Debug'), (root, 'SIDEY', 'Release')]:
                with self.subTest(target=target, source=source), self.assertRaisesRegex(RuntimeError, 'another worktree'):
                    p.claim_build_directory(directory, source, target, configuration)


if __name__ == '__main__':
    unittest.main()
