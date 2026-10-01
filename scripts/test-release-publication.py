"""Exercise the actual workflow publication validator and job condition offline."""
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import textwrap
import unittest

workflow = (Path(__file__).resolve().parents[1] / '.github/workflows/release.yml').read_text(encoding='utf-8')
publish = workflow.split('\n  publish-release:\n', 1)[1].split('\n  prune-artifacts:', 1)[0]
validator = textwrap.dedent(re.search(r"<<'PY'\n(.*?)^          PY$", publish, re.M | re.S).group(1))
condition = publish.split('    if: >-\n', 1)[1].split('    runs-on:', 1)[0].strip()
names = [f'mt-{rid}.{suffix}' for rid in ['linux-arm64', 'linux-x64', 'osx-arm64', 'osx-x64', 'win-x64', 'win-x86']
         for suffix in [('zip' if rid.startswith('win') else 'tar.gz'), 'spdx.json']]


class PublicationTests(unittest.TestCase):
    def validate(self, release, tag='v1.2.3-dev'):
        with tempfile.TemporaryDirectory(prefix='tlbx-publication-') as directory:
            path = Path(directory) / 'release.json'
            patch = Path(directory) / 'patch.json'
            path.write_text(json.dumps(release), encoding='utf-8')
            result = subprocess.run([sys.executable, '-', str(path), tag, str(patch)], input=validator,
                                    text=True, capture_output=True)
            self.patch = json.loads(patch.read_text(encoding='utf-8')) if patch.exists() else None
            return result

    def release(self):
        return {'databaseId': 42, 'tagName': 'v1.2.3-dev', 'isDraft': True, 'isPrerelease': True,
                'assets': [{'name': name, 'size': 100} for name in names]}

    def test_complete_dev_and_stable_drafts(self):
        release = self.release()
        self.assertEqual(self.validate(release).stdout.strip(), '42')
        release.update(tagName='v1.2.3', isPrerelease=False)
        self.assertEqual(self.validate(release, 'v1.2.3').stdout.strip(), '42')
        self.assertEqual(self.patch['make_latest'], 'true')
        self.assertFalse(self.patch['draft'])
        self.assertIn('Unavailable in this release: none.', self.patch['body'])

    def test_missing_macos_still_publishes_windows_and_linux(self):
        release = self.release()
        release['body'] = 'Terminal input sizing fix.'
        release['assets'] = [asset for asset in release['assets'] if 'osx-' not in asset['name']]
        self.assertEqual(self.validate(release).returncode, 0)
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
                    self.assertEqual(self.validate(release, tag).returncode, 0)

    def test_no_platform_cannot_publish(self):
        release = self.release()
        release['assets'] = []
        self.assertNotEqual(self.validate(release).returncode, 0)
        self.assertIsNone(self.patch)

    def test_incomplete_or_wrong_assets_never_publish(self):
        for mutation in [lambda r: r['assets'].pop(),
                         lambda r: r['assets'].append({'name': 'extra.zip', 'size': 1}),
                         lambda r: r['assets'][0].update(size=0),
                         lambda r: r['assets'][0].update(name=r['assets'][1]['name'])]:
            with self.subTest(mutation=mutation):
                release = self.release()
                mutation(release)
                self.assertNotEqual(self.validate(release).returncode, 0)

    def test_wrong_metadata_never_publishes(self):
        for change in [dict(isDraft=False), dict(isPrerelease=False), dict(tagName='v1.2.4-dev'), dict(databaseId=0)]:
            with self.subTest(change=change):
                release = self.release()
                release.update(change)
                self.assertNotEqual(self.validate(release).returncode, 0)

    def gate(self, dev, results):
        expression = condition.replace('always()', 'True').replace('!cancelled()', 'True')
        expression = expression.replace('needs.prepare.outputs.is_dev', repr('true' if dev else 'false'))
        for job, result in results.items():
            expression = expression.replace(f'needs.{job}.result', repr(result))
        return eval(expression.replace('&&', ' and ').replace('||', ' or ').replace('\n', ' '), {'__builtins__': {}})

    def test_platform_failures_do_not_block_shared_success(self):
        for dev in [True, False]:
            shared = ['prepare', 'frontend', 'supply-chain']
            if not dev:
                shared += ['frontend-tests', 'dotnet-tests']
            results = {job: 'success' for job in shared}
            if dev:
                results.update({'frontend-tests': 'skipped', 'dotnet-tests': 'skipped'})
            active = ['build-dev', 'build-windows-dev'] if dev else ['build', 'build-windows']
            for status in ['success', 'failure', 'skipped']:
                for job in active:
                    self.assertTrue(self.gate(dev, {**results, job: status}))
            for job in shared:
                for status in ['failure', 'cancelled', 'skipped']:
                    with self.subTest(dev=dev, job=job, status=status):
                        self.assertFalse(self.gate(dev, {**results, job: status}))

    def test_cancellation_blocks_publication(self):
        self.assertIn('!cancelled()', condition)

    def test_only_platform_jobs_tolerate_failure(self):
        for job in ['build-dev', 'build', 'build-windows-dev', 'build-windows']:
            block = re.split(r'\n  [a-z][a-z-]*:', workflow.split(f'\n  {job}:\n', 1)[1])[0]
            self.assertIn('    continue-on-error: true', block)
            self.assertIn('      fail-fast: false', block)
            self.assertLess(block.index('Generate SBOM and attest release'), block.index('Upload release archive and SBOM'))
        for job in ['prepare', 'frontend', 'frontend-tests', 'dotnet-tests', 'supply-chain', 'publish-release']:
            block = re.split(r'\n  [a-z][a-z-]*:', workflow.split(f'\n  {job}:\n', 1)[1])[0]
            self.assertNotIn('    continue-on-error: true', block)


if __name__ == '__main__':
    unittest.main()
