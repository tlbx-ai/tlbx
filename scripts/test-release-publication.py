"""Exercise release package validation directly, without GitHub or subprocesses."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("publication", Path(__file__).with_name("validate-release-publication.py"))
publication = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publication)

names = [f'mt-{rid}.{suffix}' for rid in ['linux-arm64', 'linux-x64', 'osx-arm64', 'osx-x64', 'win-x64', 'win-x86']
         for suffix in [('zip' if rid.startswith('win') else 'tar.gz'), 'spdx.json']]


class PublicationTests(unittest.TestCase):
    def validate(self, release, tag='v1.2.3-dev'):
        self.patch = publication.validate_release(release, tag)
        return self.patch

    def release(self):
        return {'databaseId': 42, 'tagName': 'v1.2.3-dev', 'isDraft': True, 'isPrerelease': True,
                'assets': [{'name': name, 'size': 100} for name in names]}

    def test_complete_dev_and_stable_drafts(self):
        release = self.release()
        self.validate(release)
        release.update(tagName='v1.2.3', isPrerelease=False)
        self.validate(release, 'v1.2.3')
        self.assertEqual(self.patch['make_latest'], 'true')
        self.assertFalse(self.patch['draft'])
        self.assertIn('Unavailable in this release: none.', self.patch['body'])

    def test_missing_macos_still_publishes_windows_and_linux(self):
        release = self.release()
        release['body'] = 'Terminal input sizing fix.'
        release['assets'] = [asset for asset in release['assets'] if 'osx-' not in asset['name']]
        self.validate(release)
        self.assertFalse(self.patch['draft'])
        self.assertTrue(self.patch['prerelease'])
        self.assertEqual(self.patch['make_latest'], 'false')
        self.assertIn('Terminal input sizing fix.', self.patch['body'])
        self.assertIn('Available: linux-arm64, linux-x64, win-x64, win-x86.', self.patch['body'])
        self.assertIn('Unavailable in this release: osx-arm64, osx-x64.', self.patch['body'])

    def test_any_single_complete_platform_is_valid(self):
        for rid in ['linux-arm64', 'linux-x64', 'osx-arm64', 'osx-x64', 'win-x64', 'win-x86']:
            for dev in [True, False]:
                with self.subTest(rid=rid, dev=dev):
                    release = self.release()
                    release['assets'] = [asset for asset in release['assets'] if f'mt-{rid}.' in asset['name']]
                    tag = 'v1.2.3-dev' if dev else 'v1.2.3'
                    release.update(tagName=tag, isPrerelease=dev)
                    self.validate(release, tag)

    def test_no_platform_cannot_publish(self):
        release = self.release()
        release['assets'] = []
        with self.assertRaises(ValueError):
            self.validate(release)

    def test_incomplete_or_wrong_assets_never_publish(self):
        for mutation in [lambda r: r['assets'].pop(),
                         lambda r: r['assets'].append({'name': 'extra.zip', 'size': 1}),
                         lambda r: r['assets'][0].update(size=0),
                         lambda r: r['assets'][0].update(name=r['assets'][1]['name'])]:
            with self.subTest(mutation=mutation):
                release = self.release()
                mutation(release)
                with self.assertRaises(ValueError):
                    self.validate(release)

    def test_wrong_metadata_never_publishes(self):
        for change in [dict(isDraft=None), dict(isPrerelease=False), dict(tagName='v1.2.4-dev'), dict(databaseId=0)]:
            with self.subTest(change=change):
                release = self.release()
                release.update(change)
                with self.assertRaises(ValueError):
                    self.validate(release)

    def test_already_published_partial_release_is_completed_idempotently(self):
        release = self.release()
        release.update(isDraft=False, body='MCP update fix.\n\n## Platform availability\n\nAvailable: linux-x64.')
        self.validate(release)
        self.assertFalse(self.patch['draft'])
        self.assertEqual(self.patch['body'].count('## Platform availability'), 1)
        self.assertIn('MCP update fix.', self.patch['body'])
        self.assertIn('Unavailable in this release: none.', self.patch['body'])

if __name__ == '__main__':
    unittest.main()
