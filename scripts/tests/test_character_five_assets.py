import importlib.util
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
from character_five_provenance import verify_character_five_provenance


def read(path):
    return (ROOT / path).read_bytes()


class ApprovedContentTests(unittest.TestCase):
    def test_promoted_files_follow_selected_original_or_recorded_derivative(self):
        final = verify_character_five_provenance(read)
        self.assertEqual(len(final), 18)
        promotion = json.loads(read('assets/v1/character-five-source.json'))
        unchanged = [item for item in promotion['files'] if 'derivative' not in item]
        self.assertEqual(len(unchanged), 8)
        for item in unchanged:
            self.assertFalse(item['destination'].startswith('assets/v1/characters/'))
            self.assertEqual(read(item['source']), read(item['destination']))

    def test_compact_revision_reproduces_all_frames_and_matches_canonical_and_web(self):
        directory = ROOT / 'docs/reviews/character-five'
        sys.path.insert(0, str(directory))
        try:
            spec = importlib.util.spec_from_file_location('compact_feet_v1', directory / 'build_compact_feet_v1.py')
            generator = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(generator)
            outputs, report, pairs = generator.build()
        finally:
            sys.path.remove(str(directory))
        self.assertEqual(generator.self_test(pairs), 6)
        self.assertEqual(report['summary']['frames'], 90)
        self.assertEqual(report['summary']['changed_frames'], 78)
        self.assertEqual(report['summary']['preserved_tucked_frames'], 12)
        for path, encoded in outputs.items():
            self.assertEqual((generator.TARGET / path).read_bytes(), encoded, path)
        manifest = json.loads(read('assets/v1/manifest.json'))
        for sheet in report['sheets']:
            character = next(item for item in manifest['characters'] if item['id'] == sheet['character_id'])
            asset = character[sheet['sheet']]
            self.assertEqual(asset['sha256'], sheet['sha256'])
            self.assertEqual(read('assets/v1/' + asset['path']), outputs[sheet['path']])
            if sheet['sheet'] == 'base':
                self.assertEqual(read(f"website/public/assets/{character['web_directory']}/{character['id']}.png"), outputs[sheet['path']])

    def test_provenance_rejects_changed_generator_result_approval_and_canonical(self):
        for path in [
            'docs/reviews/character-five/build_compact_feet_v1.py',
            'docs/reviews/character-five-compact-feet-v1/pixel_shiba/base.png',
            'docs/reviews/character-five/approvals.json',
            'assets/v1/characters/pixel_shiba/base.png',
        ]:
            with self.subTest(path=path):
                with self.assertRaises(ValueError):
                    verify_character_five_provenance(lambda requested: read(requested) + (b' ' if requested == path else b''))

    def test_provenance_rejects_invented_user_approval_and_wrong_input(self):
        revision_path = 'docs/reviews/character-five-compact-feet-v1/revision.json'
        revision = json.loads(read(revision_path))
        revision['authorization']['new_pixels_explicitly_approved_by_user'] = True
        with self.assertRaisesRegex(ValueError, 'distinguish'):
            verify_character_five_provenance(lambda path: json.dumps(revision).encode() if path == revision_path else read(path))
        promotion_path = 'assets/v1/character-five-source.json'
        promotion = json.loads(read(promotion_path))
        promotion['files'][0]['derivative']['input']['sha256'] = '0' * 64
        with self.assertRaisesRegex(ValueError, 'input'):
            verify_character_five_provenance(lambda path: json.dumps(promotion).encode() if path == promotion_path else read(path))

    def test_prior_reviewed_snapshots_without_derivatives_remain_verifiable(self):
        path = 'assets/v1/character-five-source.json'
        promotion = json.loads(read(path))
        legacy = {}
        for item in promotion['files']:
            item.pop('derivative', None)
            legacy[item['destination']] = read(item['source'])
        legacy[path] = json.dumps(promotion).encode()
        final = verify_character_five_provenance(lambda path: legacy[path] if path in legacy else read(path))
        self.assertEqual(len(final), 18)
        self.assertTrue(all('compact-feet' not in item['source'] for item in final))

    def test_new_offers_keep_independent_entitlements_and_reuse_duck(self):
        catalog = json.loads((ROOT / 'assets/v1/commerce-catalog.json').read_text())
        manifest = json.loads((ROOT / 'assets/v1/manifest.json').read_text())
        self.assertEqual((len(catalog), len(manifest['characters']), len(manifest['throwables'])), (33, 17, 19))
        self.assertEqual(len({p['entitlement'] for p in catalog}), 33)
        duck = [p for p in catalog if p['id'] == 'throwable_squeaky_duck']
        self.assertEqual(len(duck), 1)
        self.assertEqual(duck[0]['related_character_product_id'], 'character_duck')
        self.assertFalse(any('rubber_duck' in p['id'] for p in catalog))
        for animal in ['shiba', 'duck', 'poop', 'tteokbokki', 'quokka']:
            entry = next(p for p in catalog if p['id'] == 'character_' + animal)
            character = next(c for c in manifest['characters'] if c['id'] == 'pixel_' + animal)
            copy = json.loads((ROOT / f'docs/reviews/character-five/copy-v1/pixel_{animal}.json').read_text())
            self.assertEqual(entry['description'], copy['character']['description'])
            self.assertEqual(entry['direct_price'], 1100)
            self.assertIn('windows', character['supported_platforms'])
            related = [p for p in catalog if p['related_character_product_id'] == entry['id']]
            self.assertEqual(len(related), 1)
            self.assertEqual(related[0]['description'], copy['keepsake']['description'])
            self.assertNotEqual(entry['entitlement'], related[0]['entitlement'])
            throwable = next(t for t in manifest['throwables']
                             if t['id'] == related[0]['render_asset_id'])
            self.assertIn('windows', throwable['supported_platforms'])
