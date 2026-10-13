import copy
import csv
import importlib.util
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('validate_plan', ROOT / 'Docs/tools/validate_plan.py')
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


class PlanTests(unittest.TestCase):
    def setUp(self):
        self.plan = json.loads((ROOT / 'tasks.json').read_text(encoding='utf-8'))
        # Mutating graph tests need a stable TODO fixture, not live task progress.
        for task in self.plan['tasks']:
            task['status'] = 'TODO'

    def check_error(self, fragment, profile='free_launch'):
        self.assertTrue(any(fragment in error for error in VALIDATOR.validate_plan(self.plan, profile)))

    def test_imported_plan_both_profiles(self):
        self.assertEqual(len(self.plan["tasks"]), 57)
        for profile in ('free_launch', 'paid_launch'):
            self.assertEqual(VALIDATOR.validate_plan(self.plan, profile), [])

    def test_current_execution_plan_both_profiles(self):
        current = json.loads((ROOT / 'tasks.json').read_text(encoding='utf-8'))
        for profile in ('free_launch', 'paid_launch'):
            self.assertEqual(VALIDATOR.validate_plan(current, profile), [])

    def test_source_hashes(self):
        self.assertEqual(VALIDATOR.verify_sources(ROOT), [])

    def test_all_initial_tasks_are_todo(self):
        archive = json.loads((ROOT / 'Docs/Source/VRC_Katamari_Agent_Tasks_v0.3.json').read_text(encoding='utf-8'))
        self.assertEqual({t['status'] for t in archive['tasks']}, {'TODO'})

    def test_duplicate_id(self):
        self.plan['tasks'].append(copy.deepcopy(self.plan['tasks'][0]))
        self.check_error('Duplicate task')

    def test_unknown_dependency(self):
        self.plan['tasks'][0]['dependencies'] = ['T99']
        self.check_error('unknown dependency')

    def test_cycle(self):
        self.plan['tasks'][0]['dependencies'] = ['T04']
        self.check_error('Dependency cycle')

    def test_conditional_cycle(self):
        self.plan['tasks'][41]['dependencies'].append('T42')
        self.check_error('Dependency cycle', 'paid_launch')
        self.assertEqual(VALIDATOR.validate_plan(self.plan, 'free_launch'), [])

    def test_paid_requirement_cannot_disappear(self):
        self.plan['tasks'][42]['conditional_dependencies']['paid_launch'] = []
        self.check_error('Paid T42', 'paid_launch')

    def test_free_store_cannot_enable(self):
        self.plan['releaseProfiles']['free_launch']['paid_store_enabled'] = True
        self.check_error('Incorrect paid store flag')

    def test_unsafe_scope(self):
        for path in ('../outside/', '/tmp/outside/', 'Docs/../outside/', '.git/config', 'Docs\\bad'):
            self.plan['tasks'][0]['write_scope'] = [path]
            self.check_error('unsafe write scope')

    def test_unmapped_unity_scope(self):
        self.plan['tasks'][0]['write_scope'] = ['Assets/Bad/']
        self.check_error('missing VRChatKatamariDanacyUnity/')

    def test_stale_unity_root(self):
        self.plan['tasks'][0]['write_scope'] = ['UnityProject/Assets/Bad/']
        self.check_error('stale Unity root; use VRChatKatamariDanacyUnity/')

    def test_done_requires_done_dependencies(self):
        self.plan['tasks'][4]['status'] = 'DONE'
        self.check_error('dependency T00 is not DONE')

    def test_missing_acceptance(self):
        self.plan['tasks'][0]['acceptance_criteria'] = []
        self.check_error('empty acceptance_criteria')

    def test_picnic_csv_has_consistent_columns_and_ids(self):
        with (ROOT / 'Docs/Research/AssetManifest/picnic_candidates.csv').open(encoding='utf-8', newline='') as stream:
            rows = list(csv.reader(stream))
        self.assertGreater(len(rows), 1)
        self.assertEqual(len(rows[0]), 10)
        self.assertTrue(all(len(row) == len(rows[0]) for row in rows[1:]))
        self.assertEqual(len({row[0] for row in rows[1:]}), len(rows) - 1)

    def test_current_evidence_paths_exist_inside_repository(self):
        current = json.loads((ROOT / 'tasks.json').read_text(encoding='utf-8'))
        for task in current['tasks']:
            for relative in task.get('evidence_paths', []):
                target = (ROOT / relative).resolve()
                self.assertTrue(target.is_relative_to(ROOT))
                self.assertTrue(target.is_file(), relative)

    def test_configuration_stays_provisional_and_free(self):
        config = json.loads((ROOT / 'Docs/config/prototype_defaults.json').read_text(encoding='utf-8'))
        self.assertEqual(config['status'], 'PROVISIONAL_NOT_TESTED')
        self.assertFalse(config['commerce']['paidStoreEnabled'])
        self.assertFalse(config['shape']['affectsGameplayPhysics'])
        self.assertEqual(config['powerups']['slotCount'], 1)


if __name__ == '__main__':
    unittest.main()
