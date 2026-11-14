"""Offline planning and intake evidence checks, never Unity or license clearance."""
import csv
import hashlib
import json
import unittest
from collections import Counter
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[2]


class OfflinePreparationTests(unittest.TestCase):
    def setUp(self):
        self.prep = json.loads((ROOT / 'Docs/config/offline_preparation.json').read_text(encoding='utf-8'))
        self.manifest = json.loads((ROOT / 'Docs/AssetIntake/DirectDownload/manifest.json').read_text(encoding='utf-8'))

    def test_offline_boundaries_and_package_dag(self):
        self.assertIs(self.prep['unityAllowed'], False)
        self.assertIs(self.prep['publicationAllowed'], False)
        packages = self.prep['workPackages']
        by_id = {p['id']: p for p in packages}
        self.assertEqual(len(by_id), len(packages))
        milestones = {t['id'] for t in json.loads((ROOT / 'tasks.json').read_text(encoding='utf-8'))['tasks']}
        visiting, visited = set(), set()
        def visit(ident):
            self.assertNotIn(ident, visiting, 'Offline package dependency cycle')
            if ident in visited:
                return
            visiting.add(ident)
            for dep in by_id[ident]['dependencies']:
                self.assertIn(dep, by_id)
                visit(dep)
            visiting.remove(ident)
            visited.add(ident)
        for package in packages:
            self.assertIn(package['status'], {'TODO', 'READY', 'RUNNING', 'REVIEW', 'DONE', 'BLOCKED'})
            self.assertTrue(package['acceptance'])
            if package['status'] == 'DONE':
                self.assertTrue(package.get('evidencePaths'))
                for dependency in package['dependencies']:
                    self.assertEqual(by_id[dependency]['status'], 'DONE')
            self.assertTrue(set(package['relatedMilestones']) <= milestones)
            for scope in package['writeScope']:
                path = PurePosixPath(scope)
                self.assertFalse(path.is_absolute())
                self.assertNotIn('..', path.parts)
                self.assertNotIn('\\', scope)
                self.assertFalse(scope.startswith(('VRChatKatamariDanacyUnity/', 'UnityProject/', '.git/')))
            for relative in package.get('evidencePaths', []):
                target = (ROOT / relative).resolve()
                self.assertTrue(target.is_relative_to(ROOT))
                self.assertTrue(target.is_file(), relative)
            visit(package['id'])

    def test_intake_cap_and_license_evidence(self):
        packages = self.manifest['packages']
        limits = self.manifest['limits']
        self.assertLessEqual(len(packages), limits['maxPackages'])
        total = sum(p['archiveBytes'] for p in packages)
        self.assertEqual(total, limits['actualCombinedDownloadBytes'])
        self.assertLessEqual(total, limits['maxCombinedDownloadBytes'])
        for package in packages:
            text = (ROOT / package['archivePackageLicenseEvidence']['evidenceCopy']).read_text(encoding='utf-8')
            self.assertIn('CC0', text)
            self.assertIn('commercial', text)
            self.assertTrue(package['unverified'])
            self.assertRegex(package['sha256'], r'^[0-9a-f]{64}$')
            self.assertTrue(package['downloadedPath'].startswith('Docs/AssetIntake/Downloads/'))

    def test_inventory_and_concept_coverage_consistency(self):
        with (ROOT / 'Docs/AssetIntake/DirectDownload/inventory.csv').open(encoding='utf-8', newline='') as stream:
            rows = list(csv.DictReader(stream))
        counts = Counter(row['archive'] for row in rows)
        keys = [(row['archive'], row['member_path']) for row in rows]
        self.assertEqual(len(keys), len(set(keys)))
        for package in self.manifest['packages']:
            archive = Path(package['downloadedPath']).name
            self.assertEqual(counts[archive], package['archiveAudit']['memberCountIncludingDirectories'])
            self.assertEqual(sum(int(row['uncompressed_bytes']) for row in rows if row['archive'] == archive),
                             package['archiveAudit']['declaredUncompressedBytes'])
        self.assertEqual({m['candidateId'] for m in self.manifest['conceptMappings']},
                         {f'PIC-{i:02d}' for i in range(1, 37)})
        self.assertEqual(len(self.manifest['conceptMappings']), 36)

    def test_download_hashes_when_local_archives_are_present(self):
        # A public clone intentionally lacks ignored raw downloads; evidence remains usable.
        for package in self.manifest['packages']:
            path = ROOT / package['downloadedPath']
            if not path.is_file():
                continue
            self.assertEqual(path.stat().st_size, package['archiveBytes'])
            digest = hashlib.sha256()
            with path.open('rb') as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                    digest.update(chunk)
            self.assertEqual(digest.hexdigest(), package['sha256'])

    def test_new_requirements_documents_exclude_private_chat_links(self):
        for path in (ROOT / 'Docs/Planning/RequirementsAudit').glob('*.md'):
            text = path.read_text(encoding='utf-8')
            self.assertNotIn('chatgpt.com/c/', text)
            self.assertNotIn('User: Anonymous', text)


if __name__ == '__main__':
    unittest.main()
