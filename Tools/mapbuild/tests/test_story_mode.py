#!/usr/bin/env python3
"""story 模式（单人剧情区域图）与运行时预算的回归测试（unittest，无第三方依赖）。

运行：
    python3 -m unittest discover -s Tools/mapbuild/tests

为什么要单独一个文件：两张首发图是 PvP 图（交分台 / 4 出生点 / 人数计划），而计划中的
PicnicCorner 是**单人区域图**。`validate_map.py` 用 `layout.json` 的 `mode` 字段区分两者：
`mode` 缺省是 `"pvp"`（旧图的结果一字不变，本模块也钉住这一点）；`"story"` 只允许 1 个
出生点、检查出生口袋净空，并跳过 PvP 专用段。本模块的 fixture 是
`tests/fixtures/story_pocket/`（**只给测试用，不是可玩地图**）：

    python3 Tools/mapbuild/validate_map.py Tools/mapbuild/tests/fixtures/story_pocket

覆盖两类证据：(1) 一份合法的 story 图 0 error / 0 warning 通过；(2) 题面点名的每个
负例（盒超标、平面超标、小数价值、出生口袋里的道具、重复 id、拾取尺寸缺失）都被抓住。
"""

import json
import subprocess
import sys
import unittest
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent
TOOL_DIR = TESTS_DIR.parent
REPO_ROOT = TOOL_DIR.parents[1]
sys.path.insert(0, str(TOOL_DIR))

import validate_map as vm  # noqa: E402  （必须在 sys.path 调整之后导入）

FIXTURE = TESTS_DIR / "fixtures/story_pocket"
SCRIPT = TOOL_DIR / "validate_map.py"
MAPS_ROOT = REPO_ROOT / "Docs/Design/Maps"


def load_fixture():
    """每次从磁盘重读：validate() 会给 zone 注入 `_rect`，不能共用同一份 dict。"""
    return vm.load_inputs(FIXTURE / "layout.json", FIXTURE / "prop_catalog.csv")


def report_of(mutate=None):
    layout, catalog = load_fixture()
    if mutate is not None:
        mutate(layout, catalog)
    return vm.validate(layout, catalog)


def errors_of(mutate):
    return report_of(mutate).errors


def catalog_row(catalog, prop_id):
    for row in catalog["rows"]:
        if (row.get("id") or "").strip() == prop_id:
            return row
    raise AssertionError(f"fixture 目录里没有 {prop_id}")


def run_cli(*args):
    return subprocess.run([sys.executable, str(SCRIPT), *map(str, args)],
                          cwd=str(REPO_ROOT), capture_output=True, text=True)


class StoryFixturePassesTests(unittest.TestCase):
    """合法 story 图：0 error / 0 warning，并且报告里能读出 story 专属事实。"""

    def test_fixture_passes_cleanly(self):
        report = report_of()
        self.assertEqual(report.errors, [], report.errors)
        self.assertEqual(report.warnings, [], report.warnings)
        self.assertTrue(any("地图模式 story" in note for note in report.notes), report.notes)
        self.assertTrue(any("出生点 1 个，口袋净空 1 m" in note for note in report.notes),
                        report.notes)

    def test_story_layout_may_omit_pvp_sections(self):
        layout, _ = load_fixture()
        for key in vm.PVP_ONLY_LAYOUT_KEYS:
            self.assertNotIn(key, layout, f"fixture 故意不写 {key}：story 图没有这一段")
        report = report_of()
        self.assertEqual(report.errors, [])
        self.assertTrue(any("没有交分台" in note for note in report.notes), report.notes)
        self.assertTrue(any("不做出生→交分路程" in note for note in report.notes), report.notes)

    def test_story_bank_is_still_validated_when_written(self):
        """「跳过」不等于「放过坏数据」：写了 bank 就照同一份实现查。"""
        def bad_bank(layout, catalog):
            layout["bank"] = {"zone_id": "nowhere", "anchors": [], "rotation": {}}
        errors = errors_of(bad_bank)
        self.assertTrue(any("bank.zone_id：未知区域" in error for error in errors), errors)

    def test_cli_json_reports_the_runtime_budget(self):
        result = run_cli(FIXTURE, "--json")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        summary = json.loads(result.stdout)
        self.assertEqual(summary["status"], "OK")
        budget = summary["runtime_budget"]
        self.assertEqual(budget["mode"], "story")
        self.assertEqual(budget["collision_planes"], 5)
        self.assertEqual(budget["collision_planes_limit"], 8)
        self.assertEqual(budget["collision_boxes"], 2)
        self.assertEqual(budget["collision_boxes_limit"], 8)
        self.assertEqual(budget["prop_total"], 27)
        self.assertEqual(budget["absorbable_instances"], 21)
        self.assertEqual(budget["fixed_instances"], 6)
        self.assertEqual(budget["absorbable_value_units"], 84)
        self.assertAlmostEqual(budget["absorbable_ratio"], 21 / 27, places=4)


class StoryChecksCatchBadDataTests(unittest.TestCase):
    """story 专属负例：单出生点、口袋净空、PvP 段真的被跳过。"""

    def test_story_requires_exactly_one_spawn(self):
        def two_spawns(layout, catalog):
            layout["spawns"].append({
                "id": "spawn_extra", "zone_id": "lawn",
                "position": [8.0, 0.0, 8.0], "yaw_deg": 0.0})
        errors = errors_of(two_spawns)
        self.assertTrue(any("正好 1 个出生点" in error for error in errors), errors)

    def test_story_requires_a_spawn(self):
        errors = errors_of(lambda layout, catalog: layout.update(spawns=[]))
        self.assertTrue(any("正好 1 个出生点" in error for error in errors), errors)

    def test_spawn_pocket_rejects_an_absorbable_prop(self):
        def prop_in_pocket(layout, catalog):
            layout["structures"].append({
                "definition_id": "apple", "zone_id": "pocket",
                "position": [-10.0, 0.25, -9.3], "size_m": [0.5, 0.5, 0.5],
                "yaw_deg": 0.0, "traversability": {"class": "obstacle"}})
        errors = errors_of(prop_in_pocket)
        self.assertTrue(any("出生口袋 1 m 内" in error for error in errors), errors)
        self.assertTrue(any("可卷物" in error for error in errors), errors)

    def test_spawn_pocket_rejects_a_tall_fixed_prop(self):
        def wall_in_pocket(layout, catalog):
            layout["structures"].append({
                "definition_id": "boundary_wall", "zone_id": "pocket",
                "position": [-10.4, 0.6, -10.0], "size_m": [0.2, 1.2, 1.0],
                "yaw_deg": 0.0, "traversability": {"class": "obstacle"}})
        errors = errors_of(wall_in_pocket)
        self.assertTrue(any("固定立面" in error and "出生口袋" in error for error in errors),
                        errors)

    def test_low_decal_at_the_spawn_is_allowed(self):
        """0.015 m 的贴地标记低于 max_route_step_m，不是障碍——和 PicnicLawn 同一口径。"""
        def spawn_marker(layout, catalog):
            layout["structures"].append({
                "definition_id": "spawn_marker", "zone_id": "pocket",
                "position": [-10.0, 0.01, -10.0], "size_m": [1.2, 0.02, 1.2],
                "yaw_deg": 0.0})
            catalog["rows"].append({
                "id": "spawn_marker", "name_zh": "出生标记", "category": "gameplay",
                "size_tier": "l", "real_longest_dim_m": "1.2", "game_longest_dim_m": "1.2",
                "absorb_value_placeholder": "0", "count_in_map": "1", "zone_ids": "pocket",
                "sourcing_hint": "fixture", "priority": "MVP", "coverage_ref": "",
                "role": "fixed", "notes": "", "pickup_dim_m": ""})
            layout["performance_budget"]["structure_instances"] = 7
        report = report_of(spawn_marker)
        self.assertEqual(report.errors, [], report.errors)
        self.assertEqual(report.warnings, [], report.warnings)

    def test_pocket_radius_cannot_be_less_than_one_metre(self):
        def shrink(layout, catalog):
            layout["scale"]["spawn_pocket_clear_radius_m"] = 0.5
        errors = errors_of(shrink)
        self.assertTrue(any("spawn_pocket_clear_radius_m 必须 ≥" in error for error in errors),
                        errors)

    def test_scatter_region_inside_the_pocket_warns(self):
        def scatter_in_pocket(layout, catalog):
            layout["prop_scatter"].append({
                "zone_id": "pocket", "tier_band": "t0", "min_spacing_m": 1.0,
                "definitions": [{"id": "grass_seed", "count_min": 1, "count_max": 1}]})
            catalog_row(catalog, "grass_seed")["count_in_map"] = "13"
            catalog_row(catalog, "grass_seed")["zone_ids"] = "lawn;pocket"
            layout["performance_budget"]["absorbable_instances"] = 22
        report = report_of(scatter_in_pocket)
        self.assertEqual(report.errors, [], report.errors)
        self.assertTrue(any("压进 1 m 出生口袋" in warning for warning in report.warnings),
                        report.warnings)

    def test_mode_must_be_known(self):
        errors = errors_of(lambda layout, catalog: layout.update(mode="arena"))
        self.assertTrue(any("mode 必须是" in error for error in errors), errors)


class RuntimeBudgetChecksTests(unittest.TestCase):
    """第 14 组：电机容量（8 平面 / 8 盒）、整数价值、最大拾取尺寸、重复 id。"""

    def test_too_many_collision_boxes_is_an_error(self):
        def add_boxes(layout, catalog):
            for index in range(8):
                layout["structures"].append({
                    "definition_id": "picnic_basket", "zone_id": "lawn",
                    "position": [-4.0 + index * 1.8, 0.6, 9.0],
                    "size_m": [2.0, 1.2, 1.6], "yaw_deg": 0.0,
                    "collision": "box", "traversability": {"class": "obstacle"}})
            catalog_row(catalog, "picnic_basket")["count_in_map"] = "9"
            layout["performance_budget"]["structure_instances"] = 14
        errors = errors_of(add_boxes)
        self.assertTrue(any("碰撞盒 10 个超过电机容量 8 个" in error for error in errors), errors)
        self.assertTrue(any("MaxBoxCount" in error for error in errors), errors)

    def test_too_many_collision_planes_is_an_error(self):
        def add_walls(layout, catalog):
            for z in (-6.0, 0.0, 6.0):
                layout["structures"].append({
                    "definition_id": "boundary_wall",
                    "position": [0.0, 1.0, z], "size_m": [24.0, 2.0, 0.2],
                    "yaw_deg": 0.0, "collision": "plane",
                    "traversability": {"class": "obstacle"}})
            catalog_row(catalog, "boundary_wall")["count_in_map"] = "7"
            layout["performance_budget"]["structure_instances"] = 9
        # 5 + 3 = 8，正好等于上限：不许报错。
        report = report_of(add_walls)
        self.assertEqual(report.errors, [], report.errors)
        self.assertEqual(report.warnings, [], report.warnings)
        self.assertEqual(report.budget["collision_planes"], 8)

        def add_one_more(layout, catalog):
            add_walls(layout, catalog)
            layout["structures"].append({
                "definition_id": "boundary_wall",
                "position": [0.0, 1.0, -6.0], "size_m": [24.0, 2.0, 0.2],
                "yaw_deg": 0.0, "collision": "plane",
                "traversability": {"class": "obstacle"}})
            catalog_row(catalog, "boundary_wall")["count_in_map"] = "8"
            layout["performance_budget"]["structure_instances"] = 10
        errors = errors_of(add_one_more)
        self.assertTrue(any("碰撞平面 9 个超过电机容量 8 个" in error for error in errors),
                        errors)

    def test_fractional_absorb_value_is_an_error(self):
        def fractional(layout, catalog):
            catalog_row(catalog, "apple")["absorb_value_placeholder"] = "2.5"
        errors = errors_of(fractional)
        self.assertTrue(any("价值必须是整数" in error for error in errors), errors)

    def test_zero_absorb_value_is_an_error(self):
        def zero(layout, catalog):
            catalog_row(catalog, "apple")["absorb_value_placeholder"] = "0"
        errors = errors_of(zero)
        self.assertTrue(any("≥1 的整数" in error for error in errors), errors)
        self.assertTrue(any("noGain" in error for error in errors), errors)

    def test_oversized_absorbable_needs_an_explicit_pickup_size(self):
        def drop_override(layout, catalog):
            catalog_row(catalog, "gate_log_prop")["pickup_dim_m"] = ""
        errors = errors_of(drop_override)
        self.assertTrue(any("却没有声明 pickup_dim_m" in error for error in errors), errors)

    def test_pickup_size_cannot_exceed_the_declared_maximum(self):
        def too_big(layout, catalog):
            catalog_row(catalog, "gate_log_prop")["pickup_dim_m"] = "2.0"
        errors = errors_of(too_big)
        self.assertTrue(any("超过声明的最大拾取尺寸" in error for error in errors), errors)

    def test_story_requires_a_declared_max_pickup_size(self):
        def drop_scale(layout, catalog):
            del layout["scale"]["max_pickup_diameter_m"]
        errors = errors_of(drop_scale)
        self.assertTrue(any("max_pickup_diameter_m" in error for error in errors), errors)

    def test_duplicate_catalog_id_is_an_error(self):
        def duplicate(layout, catalog):
            catalog["rows"].append(dict(catalog_row(catalog, "apple")))
        errors = errors_of(duplicate)
        self.assertTrue(any("id 重复" in error for error in errors), errors)

    def test_duplicate_scatter_definition_is_an_error(self):
        def duplicate(layout, catalog):
            layout["prop_scatter"][0]["definitions"].append(
                {"id": "grass_seed", "count_min": 1, "count_max": 1})
        errors = errors_of(duplicate)
        self.assertTrue(any("同一区域里 grass_seed 出现两次" in error for error in errors), errors)

    def test_collision_shape_must_be_plane_or_box(self):
        def unknown_shape(layout, catalog):
            layout["structures"][5]["collision"] = "capsule"
        errors = errors_of(unknown_shape)
        self.assertTrue(any("collision 必须是" in error and "capsule" in error for error in errors),
                        errors)

    def test_budget_bookkeeping_must_match_the_catalog(self):
        def wrong_budget(layout, catalog):
            layout["performance_budget"]["collision_boxes"] = 7
        errors = errors_of(wrong_budget)
        self.assertTrue(any("performance_budget.collision_boxes=7" in error for error in errors),
                        errors)

    def test_pvp_out_of_capacity_is_a_warning_not_an_error(self):
        """旧 PvP 图没有 collision 声明；一旦声明且超标，PvP 只警告，story 才报错。"""
        layout, catalog = vm.load_inputs(MAPS_ROOT / "ToyRoom" / "layout.json",
                                         MAPS_ROOT / "ToyRoom" / "prop_catalog.csv")
        for structure in layout["structures"][:9]:
            structure["collision"] = "box"
        report = vm.validate(layout, catalog)
        self.assertEqual(report.errors, [], report.errors)
        self.assertTrue(any("碰撞盒 9 个超过电机容量 8 个" in warning
                            for warning in report.warnings), report.warnings)


class PvpResultsDoNotChangeTests(unittest.TestCase):
    """`mode` 缺省 = pvp：两张首发图的每一个结果字段都和显式写 "pvp" 时完全一致。"""

    def test_pvp_default_matches_explicit_pvp(self):
        for name in ("PicnicLawn", "ToyRoom"):
            with self.subTest(map=name):
                base = MAPS_ROOT / name
                layout_a, catalog_a = vm.load_inputs(base / "layout.json",
                                                     base / "prop_catalog.csv")
                layout_b, catalog_b = vm.load_inputs(base / "layout.json",
                                                     base / "prop_catalog.csv")
                layout_b["mode"] = "pvp"
                first = vm.validate(layout_a, catalog_a)
                second = vm.validate(layout_b, catalog_b)
                self.assertEqual(first.errors, second.errors)
                self.assertEqual(first.warnings, second.warnings)
                self.assertEqual(first.notes, second.notes)
                self.assertEqual(first.surfaces, second.surfaces)
                self.assertEqual(first.obstacles, second.obstacles)
                self.assertEqual(first.budget, second.budget)
                self.assertEqual(first.budget["mode"], "pvp")

    def test_shipping_maps_report_fixed_vs_absorbable_counts(self):
        for name, total, absorbable, fixed, units in (
                ("PicnicLawn", 512, 452, 60, 1284),
                ("ToyRoom", 479, 440, 39, 1284)):
            with self.subTest(map=name):
                report = vm.validate(*vm.load_inputs(MAPS_ROOT / name / "layout.json",
                                                     MAPS_ROOT / name / "prop_catalog.csv"))
                budget = report.budget
                self.assertEqual(budget["prop_total"], total)
                self.assertEqual(budget["absorbable_instances"], absorbable)
                self.assertEqual(budget["fixed_instances"], fixed)
                self.assertEqual(budget["absorbable_value_units"], units)
                self.assertTrue(any(f"道具 {total} 件" in note for note in report.notes),
                                report.notes)
                self.assertTrue(any("可卷" in note and f"{units} 单位" in note
                                    for note in report.notes), report.notes)


class StoryFixtureIsNotAMapTests(unittest.TestCase):
    """fixture 只活在测试目录里：不能被当成 DESIGN 地图，也不该有专用校验器。"""

    def test_fixture_lives_under_tests(self):
        self.assertTrue(FIXTURE.is_dir())
        self.assertTrue((FIXTURE / "layout.json").is_file())
        self.assertTrue((FIXTURE / "prop_catalog.csv").is_file())
        self.assertFalse((MAPS_ROOT / "story_fixture_pocket").exists(),
                         "fixture 不是真地图，不要放进 Docs/Design/Maps")

    def test_no_per_map_validator_in_the_fixture(self):
        leftovers = sorted(str(path.relative_to(TOOL_DIR))
                           for path in FIXTURE.glob("validate_*.py"))
        self.assertEqual(leftovers, [])

    def test_fixture_copy_is_independent(self):
        """两次 read 不能共用可变 dict：validate() 会往 zone 里写 `_rect`。"""
        first, _ = load_fixture()
        second, _ = load_fixture()
        self.assertIsNot(first, second)
        self.assertIsNot(first["zones"][0], second["zones"][0])


if __name__ == "__main__":
    unittest.main()
