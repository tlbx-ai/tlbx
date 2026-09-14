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
            path.write_text(json.dumps(release), encoding='utf-8')
            return subprocess.run([sys.executable, '-', str(path), tag], input=validator,
                                  text=True, capture_output=True)

    def release(self):
        return {'databaseId': 42, 'tagName': 'v1.2.3-dev', 'isDraft': True, 'isPrerelease': True,
                'assets': [{'name': name, 'size': 100} for name in names]}

    def test_complete_dev_and_stable_drafts(self):
        release = self.release()
        self.assertEqual(self.validate(release).stdout.strip(), '42')
        release.update(tagName='v1.2.3', isPrerelease=False)
        self.assertEqual(self.validate(release, 'v1.2.3').stdout.strip(), '42')

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
        expression = condition.replace('always()', 'True')
        expression = expression.replace('needs.prepare.outputs.is_dev', repr('true' if dev else 'false'))
        for job, result in results.items():
            expression = expression.replace(f'needs.{job}.result', repr(result))
        return eval(expression.replace('&&', ' and ').replace('||', ' or ').replace('\n', ' '), {'__builtins__': {}})

    def test_only_the_successful_channel_can_publish(self):
        for dev in [True, False]:
            active = ['prepare', 'build-dev', 'build-windows-dev'] if dev else ['prepare', 'build', 'build-windows']
            results = {job: ('success' if job in active else 'skipped')
                       for job in ['prepare', 'build-dev', 'build-windows-dev', 'build', 'build-windows']}
            self.assertTrue(self.gate(dev, results))
            for job in active:
                for status in ['failure', 'cancelled', 'skipped']:
                    with self.subTest(dev=dev, job=job, status=status):
                        self.assertFalse(self.gate(dev, {**results, job: status}))


if __name__ == '__main__':
    unittest.main()
