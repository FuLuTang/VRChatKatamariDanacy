"""剧情关卡数据必须自洽，否则 python3 -m unittest discover -s Docs/tests 变红。

这里只跑 Docs/Design/GameModes/validate_levels.py 的规则，不碰 Unity、Udon 或运行时：
关卡 JSON / categories.json 是作者数据，坏了就是数据坏了，不能靠肉眼复核。

注意：地图（layout.json / prop_catalog.csv）一改，本测试会因 map_snapshot 失配而失败。
这是设计意图（README §0.2/§4.5 规则 4）：复核目标后把新 sha256 写进关卡再提交。
"""
import copy
import csv
import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
GAMEMODES = ROOT / 'Docs/Design/GameModes'
SPEC = importlib.util.spec_from_file_location('validate_levels', GAMEMODES / 'validate_levels.py')
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)

LEVEL_IDS = ('story_01_bench_size', 'story_02_fruit_ten')


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class LevelDataTests(unittest.TestCase):
    def setUp(self):
        self.levels = [(path.name, json.loads(path.read_text(encoding='utf-8')))
                       for path in sorted((GAMEMODES / 'Levels').glob('*.json'))]
        self.categories = json.loads((GAMEMODES / 'categories.json').read_text(encoding='utf-8'))

    def level(self, level_id):
        for _, level in self.levels:
            if level['level_id'] == level_id:
                return level
        self.fail(f'missing level {level_id}')

    def run_with(self, mutate):
        levels = copy.deepcopy(self.levels)
        categories = copy.deepcopy(self.categories)
        mutate({level['level_id']: level for _, level in levels}, categories)
        return VALIDATOR.validate(levels=levels, categories=categories)

    def assert_error(self, fragment, mutate):
        report = self.run_with(mutate)
        self.assertTrue(any(fragment in error for error in report.errors),
                        f'expected {fragment!r}, got {report.errors}')
        return report

    # ---- 现状必须通过 ----
    def test_current_level_data_passes(self):
        report = VALIDATOR.validate()
        self.assertEqual(report.errors, [])
        self.assertEqual(sorted(report.summary['levels']), sorted(LEVEL_IDS))

    def test_two_levels_are_ordered_story_levels(self):
        for index, level_id in enumerate(LEVEL_IDS):
            level = self.level(level_id)
            self.assertEqual(level['mode'], 'story')
            self.assertEqual(level['order'], index + 1)
            self.assertEqual(level['map_id'], 'picnic_lawn')
            self.assertEqual((level['player']['min'], level['player']['max']), (1, 1))
            self.assertIs(level['bank']['enabled'], False)
            self.assertIs(level['powerups']['enabled'], False)
            self.assertIs(level['evaluation']['ledger_write'], False)
            self.assertEqual(level['evaluation']['score_channel'], 'none')

    def test_objectives_and_stars_are_the_designed_ones(self):
        first = self.level('story_01_bench_size')
        self.assertEqual(first['quest']['type'], 'reach_target_diameter')
        self.assertEqual(first['quest']['objective'], {'target_diameter_m': 1.25})
        self.assertEqual([star['requires']['target_diameter_m'] for star in first['stars']],
                         [1.25, 1.45, 1.6])
        self.assertEqual(first['time_limit_s'], 120)
        self.assertEqual(first['spawn_id'], 'spawn_nw')

        second = self.level('story_02_fruit_ten')
        self.assertEqual(second['quest']['type'], 'collect_category_count')
        self.assertEqual(second['quest']['objective'], {'category_id': 'fruit', 'count': 10})
        self.assertEqual([star['requires']['count'] for star in second['stars']], [10, 20, 30])
        self.assertEqual(second['time_limit_s'], 120)
        self.assertEqual(second['spawn_id'], 'spawn_ne')

    def test_snapshot_matches_the_files_on_disk(self):
        layout = sha256(ROOT / 'Docs/Design/Maps/PicnicLawn/layout.json')
        catalog = sha256(ROOT / 'Docs/Design/Maps/PicnicLawn/prop_catalog.csv')
        for _, level in self.levels:
            self.assertEqual(level['map_snapshot']['layout_sha256'], layout)
            self.assertEqual(level['map_snapshot']['catalog_sha256'], catalog)

    def test_categories_reference_only_real_catalog_ids(self):
        catalog = set()
        with (ROOT / 'Docs/Design/Maps/PicnicLawn/prop_catalog.csv').open(
                encoding='utf-8', newline='') as stream:
            for row in csv.DictReader(stream):
                catalog.add(row['id'])
        for category in self.categories['categories'].values():
            self.assertTrue(category['definition_ids'])
            self.assertTrue(set(category['definition_ids']) <= catalog)
        self.assertIn('fruit', self.categories['categories'])

    def test_model_estimate_leaves_room_for_three_times_margin(self):
        report = VALIDATOR.validate()
        for level_id, facts in report.summary['levels'].items():
            estimate = facts['model_meet_time_s']
            self.assertIsNotNone(estimate, level_id)
            self.assertGreater(estimate, 5.0, level_id)
            self.assertLessEqual(3.0 * estimate, facts['time_limit_s'], level_id)

    # ---- 坏数据必须被抓住 ----
    def test_stale_map_snapshot_is_reported(self):
        report = self.assert_error('地图已变更', lambda levels, cats:
                                   levels['story_01_bench_size']['map_snapshot'].update(
                                       {'catalog_sha256': '0' * 64}))
        self.assertTrue(any('prop_catalog.csv' in error for error in report.errors))

    def test_bank_must_stay_disabled(self):
        self.assert_error('bank.enabled', lambda levels, cats:
                          levels['story_02_fruit_ten']['bank'].update({'enabled': True}))

    def test_player_count_must_be_single(self):
        self.assert_error('player 必须是 min=max=1', lambda levels, cats:
                          levels['story_01_bench_size']['player'].update({'max': 2}))

    def test_star_thresholds_must_strictly_increase(self):
        self.assert_error('严格递增', lambda levels, cats:
                          levels['story_01_bench_size']['stars'][2]['requires'].update(
                              {'target_diameter_m': 1.45}))

    def test_first_star_must_equal_objective(self):
        self.assert_error('第 1 星阈值', lambda levels, cats:
                          levels['story_01_bench_size']['stars'][0]['requires'].update(
                              {'target_diameter_m': 1.3}))

    def test_reach_target_must_match_the_declared_gate(self):
        def mutate(levels, cats):
            level = levels['story_01_bench_size']
            level['quest']['objective']['target_diameter_m'] = 1.3
            level['quest']['text_zh'] = '目标改成 1.3 m 的对照实验。'
            for star in level['stars']:
                star['requires']['target_diameter_m'] += 0.05
        self.assert_error('教学承诺失效', mutate)

    def test_unknown_zone_is_rejected(self):
        self.assert_error("不存在的区域 'core_lavm'", lambda levels, cats:
                          levels['story_01_bench_size']['map_expectations']['zones'].append(
                              'core_lavm'))

    def test_unknown_size_gate_is_rejected(self):
        self.assert_error('不存在的尺寸门', lambda levels, cats:
                          levels['story_01_bench_size']['map_expectations'][
                              'size_gate_ids'].append('gate_bench_xx'))

    def test_unknown_spawn_is_rejected(self):
        self.assert_error('spawn_id', lambda levels, cats:
                          levels['story_02_fruit_ten'].update({'spawn_id': 'spawn_north'}))

    def test_coordinates_in_level_files_are_rejected(self):
        self.assert_error('几何键', lambda levels, cats:
                          levels['story_01_bench_size'].update(
                              {'spawn_position': {'x': -14.5, 'z': 14.5}}))

    def test_per_definition_counts_are_rejected(self):
        # 逐定义件数只能来自 prop_catalog.csv，关卡里出现就是抄了一个会过期的数。
        self.assert_error('未知字段', lambda levels, cats:
                          levels['story_02_fruit_ten']['quest'].update(
                              {'basket_north_fruit': 23}))
        self.assert_error('只允许 target_diameter_m', lambda levels, cats:
                          levels['story_02_fruit_ten']['quest'].update(
                              {'fruit_radius_m': 0.06}))

    def test_category_definition_must_exist_in_catalog(self):
        self.assert_error('不在 prop_catalog.csv', lambda levels, cats:
                          cats['categories']['fruit']['definition_ids'].append('durian'))

    def test_used_by_levels_must_match_reality(self):
        self.assert_error('used_by_levels', lambda levels, cats:
                          cats['categories']['fruit']['used_by_levels'].append(
                              'story_01_bench_size'))

    def test_collect_more_than_map_supply_is_rejected(self):
        def mutate(levels, cats):
            levels['story_02_fruit_ten']['quest']['objective']['count'] = 60
            for star in levels['story_02_fruit_ten']['stars']:
                star['requires']['count'] = 60 + 10 * star['stars']
        self.assert_error('数量上不可达', mutate)

    def test_collect_target_reachable_from_spawn_is_rejected(self):
        # 出生角的 12 件全是 s 档杂项：拿它当收集类别的关卡等于站着不动就完成。
        def mutate(levels, cats):
            cats['categories']['corner_litter'] = {
                'name_zh': '出生角杂项',
                'definition_ids': ['seed', 'bottle_cap', 'paper_scrap'],
                'exclude_definition_ids': [],
                'notes_zh': '测试用：专门验证「出生角就能完成」这条规则。',
                'used_by_levels': ['story_02_fruit_ten'],
            }
            level = levels['story_02_fruit_ten']
            level['quest']['objective'] = {'category_id': 'corner_litter', 'count': 5}
            for star in level['stars']:
                star['requires'] = {'category_id': 'corner_litter',
                                    'count': 5 * star['stars']}
        self.assert_error('站着不动就能完成', mutate)

    def test_reach_target_above_zone_budget_is_rejected(self):
        def mutate(levels, cats):
            level = levels['story_01_bench_size']
            level['quest']['objective']['target_diameter_m'] = 2.0
            for star in level['stars']:
                star['requires']['target_diameter_m'] = 1.9 + 0.05 * star['stars']
        self.assert_error('超过相关区合计', mutate)

    def test_time_limit_out_of_range_is_rejected(self):
        self.assert_error('超出 30–300', lambda levels, cats:
                          levels['story_01_bench_size'].update({'time_limit_s': 10}))

    def test_time_limit_far_beyond_no_restock_is_warned(self):
        report = self.run_with(lambda levels, cats:
                               levels['story_01_bench_size'].update({'time_limit_s': 180}))
        self.assertEqual(report.errors, [])
        self.assertTrue(any('没有补货' in warning for warning in report.warnings))

    def test_duplicate_order_is_rejected(self):
        self.assert_error('重复（同一 map_id 内必须唯一）', lambda levels, cats:
                          levels['story_02_fruit_ten'].update(
                              {'order': levels['story_01_bench_size']['order']}))

    def test_unknown_field_is_rejected(self):
        self.assert_error('未知字段', lambda levels, cats:
                          levels['story_01_bench_size'].update({'difficulty': 'easy'}))

    def test_map_totals_must_match_the_snapshot(self):
        self.assert_error('map_expectations.absorbable_instances_total',
                          lambda levels, cats:
                          levels['story_02_fruit_ten']['map_expectations'].update(
                              {'absorbable_instances_total': 448}))


    # ---- 模型默认值必须跟着活地图走，而不是内置快照 ----
    def test_model_has_no_snapshot_and_reads_the_live_map(self):
        model = VALIDATOR._rm()
        self.assertFalse(hasattr(model, 'DEFAULT_MAP'))
        self.assertFalse(hasattr(model, 'DEFAULT_MAP_SPEC'))
        with self.assertRaises(FileNotFoundError):
            model.load_map('/nonexistent/picnic_lawn')
        live = model.load_map()
        info = model.map_fingerprint(live)
        self.assertEqual(info['absorbable_instances_total'], 452)
        self.assertEqual(info['gain_units_total'], 1284)
        self.assertEqual(info['layout_sha256'],
                         sha256(ROOT / 'Docs/Design/Maps/PicnicLawn/layout.json'))

    def test_changing_the_map_changes_the_model_default(self):
        # 在内存副本里把占位价值全部改成 1：模型默认答案必须跟着变。
        # 如果它偷偷用内置快照，absorbed_units 不会变，这条断言就会红。
        model = VALIDATOR._rm()
        params = model.RoundParams(duration_s=120.0,
                                   profiles=(model.PlayerProfile('P1'),))
        policy = VALIDATOR._no_bank_policy()
        default = model.simulate_round(params, policy, seed=7)
        self.assertEqual(default.players[0].spawn_zone, 'spawn_nw')

        map_dir = ROOT / 'Docs/Design/Maps/PicnicLawn'
        layout = json.loads((map_dir / 'layout.json').read_text(encoding='utf-8'))
        with (map_dir / 'prop_catalog.csv').open(encoding='utf-8', newline='') as stream:
            reader = csv.DictReader(stream)
            fields = reader.fieldnames
            rows = list(reader)
        for row in rows:
            row['absorb_value_placeholder'] = '1'
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            (tmp / 'layout.json').write_text(json.dumps(layout, ensure_ascii=False, indent=2),
                                             encoding='utf-8')
            with (tmp / 'prop_catalog.csv').open('w', encoding='utf-8', newline='') as stream:
                writer = csv.DictWriter(stream, fieldnames=fields)
                writer.writeheader()
                writer.writerows(rows)
            edited = model.load_map(tmp)
            self.assertEqual(edited.units_total, edited.absorbable_total)
            self.assertNotEqual(edited.units_total, model.load_map().units_total)
            reduced = model.simulate_round(params, policy,
                                           edited.map_spec(('spawn_nw',)), seed=7)
        self.assertLess(reduced.players[0].absorbed_units,
                        default.players[0].absorbed_units - 100.0)

    # ---- 第 3 星的「离开路线」必须显式声明 ----
    def test_three_star_above_the_route_requires_a_design_note(self):
        report = VALIDATOR.validate()
        self.assertEqual(report.warnings, [])
        self.assertTrue(any('离路线目标' in note for note in report.notes))

        without_note = self.run_with(lambda levels, cats:
                                     levels['story_01_bench_size']['stars'][2].pop(
                                         'design_note_zh'))
        self.assertTrue(any('必须离开所列路线' in warning
                            for warning in without_note.warnings))

    def test_stale_three_star_design_note_is_rejected(self):
        def mutate(levels, cats):
            level = levels['story_01_bench_size']
            level['stars'][2]['requires']['target_diameter_m'] = 1.46
        self.assert_error('design_note_zh 已过时', mutate)

    def test_design_note_is_not_accepted_below_the_third_star(self):
        self.assert_error('只允许出现在第 3 星', lambda levels, cats:
                          levels['story_01_bench_size']['stars'][0].update(
                              {'design_note_zh': '第 1 星也写说明是无效的。'}))


if __name__ == '__main__':
    unittest.main()
