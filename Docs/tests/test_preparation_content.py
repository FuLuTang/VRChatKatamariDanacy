"""Structure checks for text-only preparation. Does not validate translations or gameplay."""
import csv
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def rows(relative):
    with (ROOT / relative).open(encoding='utf-8', newline='') as stream:
        reader=csv.DictReader(stream)
        result=list(reader)
        for row in result:
            if None in row or any(value is None for value in row.values()):
                raise AssertionError('Malformed CSV width: '+relative)
        return result


class PreparationContentTests(unittest.TestCase):
    def test_terminology_unique_nonempty_and_brand_boundary(self):
        table=rows('Docs/Preparation/Terminology/glossary.csv')
        indexed={row['stable_id']:row for row in table}
        self.assertEqual(len(table),len(indexed))
        self.assertTrue(all(all(row.values()) for row in table))
        self.assertEqual(indexed['term.rolling_ball']['en'],'Rolling Ball')
        self.assertEqual(indexed['term.katamari']['context'],'internal_only')
        for key in ('term.unbanked_growth','term.banked_score','term.predicted_pickup',
                    'term.confirmed_pickup','term.leave_match','term.platform_respawn'):
            self.assertIn(key,indexed)

    def test_four_spawn_zones_for_each_theme_and_unknown_measurements(self):
        table=rows('Docs/Preparation/MapRubrics/spawn_fairness.csv')
        self.assertEqual({(r['map_theme'],r['spawn_zone_label']) for r in table},
                         {(theme,'Zone '+zone) for theme in ('picnic_lawn','toyroom') for zone in 'ABCD'})
        for row in table:
            self.assertEqual(row['status'],'NOT_RUN')
            for key in ('reachable_confirmed_gain_volume_units','route_to_bank_anchor_m',
                        'seconds_to_next_growth_band','spawn_ball_collision_diameter_world_m'):
                self.assertIn(row[key],('', 'unknown'))

    def test_dual_exits_and_maximum_ball_coverage(self):
        table=rows('Docs/Preparation/MapRubrics/route_checks.csv')
        for theme in ('picnic_lawn','toyroom'):
            cases={r['case_id'] for r in table if r['map_theme']==theme}
            for zone in 'ABCD':
                for number in (1,2):
                    self.assertIn(f'spawn_{zone}_exit_{number}',cases)
            self.assertIn('max_ball_main_route',cases)
        for row in table:
            self.assertEqual(row['status'],'NOT_RUN')
            self.assertIn(row['min_clear_width_m'],('', 'unknown'))

    def test_restock_template_not_claimed_tested(self):
        table=rows('Docs/Preparation/MapRubrics/resources.csv')
        self.assertEqual(len(table),8)
        for row in table:
            self.assertEqual(row['status'],'NOT_RUN')
            for key in ('gain_volume_units','spawn_generation','confirmed_spawn_time_s',
                        'duplicate_gain_check'):
                self.assertIn(row[key],('', 'unknown'))

    def test_input_experiment_matrix_structure_is_not_execution_evidence(self):
        relative='Docs/Preparation/InputExperiments/cases.csv'
        with (ROOT / relative).open(encoding='utf-8', newline='') as stream:
            reader=csv.DictReader(stream)
            header=reader.fieldnames
            table=list(reader)
        self.assertEqual(len(header),27)
        self.assertEqual(len(table),24)
        self.assertEqual(len({row['case_id'] for row in table}),24)
        for row in table:
            self.assertEqual(len(row),27)
            self.assertEqual(row['observed'],'')
            self.assertEqual(row['evidence'],'')
            self.assertEqual(row['status'],'NOT_RUN')

    def test_feedback_event_completeness_and_prediction_boundary(self):
        table=rows('Docs/Preparation/Feedback/events.csv')
        indexed={row['exampleEventId']:row for row in table}
        self.assertEqual(len(table),len(indexed))
        self.assertTrue(all(all(row.values()) for row in table))
        for key in ('collect.predicted','collect.confirmed','collect.rejected','bank.committed',
                    'combat.ko-confirmed','respawn.started','round.settling','round.reset',
                    'lobby.exit-and-recovery'):
            self.assertIn(key,indexed)
        self.assertIn('no reward',indexed['collect.predicted']['confirmationRequirement'])
        self.assertIn('None',indexed['collect.rejected']['haptic'])
        self.assertTrue(all(row['lowFXMustRetain'].strip() for row in table))


if __name__ == '__main__':
    unittest.main()
