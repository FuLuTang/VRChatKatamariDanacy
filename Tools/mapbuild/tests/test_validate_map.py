#!/usr/bin/env python3
"""validate_map.py 的回归测试（unittest，无第三方依赖）。

运行：
    python3 -m unittest discover -s Tools/mapbuild/tests
    python3 -m unittest discover -s Tools/mapbuild/tests -p "test_validate_map.py" -v

为什么有这个文件：`validate_map.py` 取代了野餐草坪那份「每图一份」的专用校验器
（419 行、只认单图，已于 2026-11-14 删除）。既然它是唯一的作者层实现，就必须有两类
证据：(1) **每张图都通过**（参数化，新图放进 `Docs/Design/Maps/` 就自动纳入）；
(2) **坏数据仍然被抓住**（负例覆盖旧校验器原来的每一类检查 + 新版多出来的规则）。
"""

import copy
import csv
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

MAPS_ROOT = REPO_ROOT / "Docs/Design/Maps"
SCRIPT = TOOL_DIR / "validate_map.py"

# 两张首发图必须有 0 error / 0 warning；未来的图只要求 0 error（warning 允许解释性保留）。
SHIPPING_MAPS = ("PicnicLawn", "ToyRoom")


def map_dirs():
    return sorted(path.parent for path in MAPS_ROOT.glob("*/layout.json"))


def load(map_dir):
    layout, catalog = vm.load_inputs(map_dir / "layout.json", map_dir / "prop_catalog.csv")
    return layout, catalog


def run_cli(*args):
    return subprocess.run([sys.executable, str(SCRIPT), *map(str, args)],
                          cwd=str(REPO_ROOT), capture_output=True, text=True)


def errors_of(layout, catalog):
    return vm.validate(layout, catalog).errors


class EveryMapPassesTests(unittest.TestCase):
    """参数化：仓库里每张图都必须过通用校验器。"""

    def test_maps_are_discovered(self):
        names = {path.name for path in map_dirs()}
        self.assertTrue(set(SHIPPING_MAPS) <= names,
                        f"首发两张图必须在 {MAPS_ROOT}：找到 {sorted(names)}")

    def test_every_map_passes(self):
        for map_dir in map_dirs():
            with self.subTest(map=map_dir.name):
                layout, catalog = load(map_dir)
                report = vm.validate(layout, catalog)
                self.assertEqual(report.errors, [], f"{map_dir.name}: {report.errors}")

    def test_shipping_maps_have_no_warnings(self):
        for name in SHIPPING_MAPS:
            with self.subTest(map=name):
                layout, catalog = load(MAPS_ROOT / name)
                report = vm.validate(layout, catalog)
                self.assertEqual(report.warnings, [], f"{name}: {report.warnings}")

    def test_cli_exit_codes(self):
        for name in SHIPPING_MAPS:
            with self.subTest(map=name):
                result = run_cli(MAPS_ROOT / name)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("结论: OK", result.stdout)
        missing = run_cli(MAPS_ROOT / "NoSuchMap")
        self.assertEqual(missing.returncode, 2, missing.stdout + missing.stderr)

    def test_json_summary_matches_the_shared_capacity_model(self):
        """--json 的容量数字必须来自 build_greybox.scatter_feasibility()，不能另写一份。"""
        import build_greybox as bg  # 延迟导入：与 validate_map 用的是同一个模块

        for name in SHIPPING_MAPS:
            with self.subTest(map=name):
                result = run_cli(MAPS_ROOT / name, "--json")
                self.assertEqual(result.returncode, 0, result.stderr)
                summary = json.loads(result.stdout)
                self.assertEqual(summary["status"], "OK")
                layout, catalog = load(MAPS_ROOT / name)
                zones = {zone["id"]: {**zone, "_rect": vm.rect_of(zone)}
                         for zone in layout["zones"]}
                props = {row["id"].strip(): {"dim": float(row["game_longest_dim_m"])}
                         for row in catalog["rows"]}
                expected = {}
                for region in layout["prop_scatter"]:
                    info = bg.scatter_feasibility(zones[region["zone_id"]], region, props)
                    expected[region["zone_id"]] = (info["count"], info["capacity_consumer"],
                                                   info["capacity_optimal"])
                got = {row["zone_id"]: (row["count"], row["capacity_consumer"],
                                        row["capacity_optimal"])
                       for row in summary["scatter"]}
                self.assertEqual(got, expected)


class BadDataIsCaughtTests(unittest.TestCase):
    """负例：旧专用校验器查过的每一类，通用校验器都要继续抓住。"""

    def setUp(self):
        self.layout, self.catalog = load(MAPS_ROOT / "ToyRoom")

    def run_with(self, mutate):
        layout = copy.deepcopy(self.layout)
        catalog = copy.deepcopy(self.catalog)
        mutate(layout, catalog)
        return errors_of(layout, catalog)

    def assert_caught(self, fragment, mutate):
        errors = self.run_with(mutate)
        self.assertTrue(any(fragment in error for error in errors),
                        f"expected {fragment!r}, got {errors}")
        return errors

    # ---- 目录 ----
    def test_catalog_id_duplicate_and_enums(self):
        self.assert_caught("id 重复", lambda l, c: c["rows"].append(dict(c["rows"][0])))
        self.assert_caught("size_tier 必须是", lambda l, c: c["rows"][0].update(size_tier="xl"))
        self.assert_caught("role 必须是", lambda l, c: c["rows"][0].update(role="decor"))
        self.assert_caught("缺列", lambda l, c: c["columns"].remove("notes"))

    def test_catalog_count_must_match_layout(self):
        self.assert_caught("目录 count_in_map", lambda l, c: c["rows"][0].update(
            count_in_map=str(int(c["rows"][0]["count_in_map"]) + 1)))

    def test_catalog_zone_ids_must_match_actual_placement(self):
        def mutate(layout, catalog):
            for row in catalog["rows"]:
                if row["id"] == "marble":
                    row["zone_ids"] = row["zone_ids"] + ";bay_north"
        self.assert_caught("zone_ids 与实际落点不一致", mutate)

    # ---- 区域 ----
    def test_zone_overlap_area_and_bounds(self):
        def overlap(layout, catalog):
            layout["zones"][1]["shape"]["max_z"] = 13.5   # bay_north 压进 blocks_north
        self.assert_caught("区域重叠（未声明）", overlap)

        def area(layout, catalog):
            layout["zones"][0]["area_m2"] = 1.0
        self.assert_caught("area_m2=", area)

        def out_of_bounds(layout, catalog):
            layout["zones"][0]["shape"]["max_x"] = 30
        self.assert_caught("矩形超出 bounds", out_of_bounds)

    def test_zone_purpose_and_surface_are_required(self):
        self.assert_caught("purpose 不能为空",
                           lambda l, c: l["zones"][0].update(purpose=""))
        self.assert_caught("surface 不能为空",
                           lambda l, c: l["zones"][0].update(surface=""))

    def test_zone_overlap_exemption_needs_a_reason(self):
        def overlap(layout, catalog):
            layout["zones"][1]["shape"]["max_z"] = 13.5
            layout["zone_overlap_exemptions"] = [
                {"a": "bay_north", "b": "blocks_north", "reason": ""}]
        self.assert_caught("必须写 reason", overlap)

    # ---- 出生点 / 交分台 ----
    def test_spawn_must_be_inside_its_zone(self):
        self.assert_caught("出生点不在自己声明的区域",
                           lambda l, c: l["spawns"][0].update(position=[0, 0, 0]))

    def test_spawn_needs_two_exits(self):
        def drop(layout, catalog):
            layout["routes"] = [r for r in layout["routes"]
                                if r.get("spawn_id") != "spawn_nw" or r["id"] != "spawn_nw_b"]
        self.assert_caught("至少要 2 条独立出路", drop)

    def test_bank_rotation_must_be_a_permutation(self):
        self.assert_caught("不是锚点集合的排列", lambda l, c: l["bank"]["rotation"].update(
            order=["bank_a", "bank_b", "bank_c", "bank_a"]))

    def test_bank_pads_must_not_overlap(self):
        def overlap(layout, catalog):
            for anchor in layout["bank"]["anchors"]:
                anchor["position"] = [0.0, 0.0, 0.0]
        self.assert_caught("交分台重叠", overlap)

    # ---- 尺寸门 ----
    def test_gate_thresholds(self):
        self.assert_caught("必须小于开启尺寸", lambda l, c: l["size_gates"][0].update(
            blocks_ball_diameter_below_m=1.6))
        self.assert_caught("超过最大球径", lambda l, c: l["size_gates"][0].update(
            absorbable_at_ball_diameter_m=2.6))
        self.assert_caught("必须是可卷物", lambda l, c: l["size_gates"][0].update(
            definition_id="cabinet"))

    def test_climb_gate_must_sit_on_raised_ground(self):
        def flatten(layout, catalog):
            for zone in layout["zones"]:
                if zone["id"] == "playmat_ne":
                    zone["ground_y_m"] = 0.0
        self.assert_caught("不是抬高地形", flatten)

    # ---- 散布与容量 ----
    def test_scatter_counts_and_spacing(self):
        self.assert_caught("count_max=3 小于", lambda l, c: l["prop_scatter"][0]["definitions"][0].update(
            count_min=400))
        self.assert_caught("小于区内最大两件的脚印间距",
                           lambda l, c: l["prop_scatter"][4].update(min_spacing_m=0.30))
        def over_capacity(layout, catalog):
            for region in layout["prop_scatter"]:
                if region["zone_id"] == "bay_north":
                    region["definitions"][0].update(count_min=7, count_max=7)  # 10 → 16 件，上限 14
        self.assert_caught("超过随机顺序摆放上限", over_capacity)

    def test_scatter_region_band_must_fit_its_zone(self):
        self.assert_caught("落在区域档位", lambda l, c: l["prop_scatter"][0].update(tier_band="t3"))

    # ---- 路线 / 咽喉 ----
    def test_route_width_and_references(self):
        self.assert_caught("净宽 1.8 m 小于最大球径",
                           lambda l, c: l["routes"][0].update(min_clear_width_m=1.8))
        self.assert_caught("路点必须是 [x, z]",
                           lambda l, c: l["routes"][0].update(waypoints=[[0, 0], [0]]))
        self.assert_caught("gate_id 引用不存在的尺寸门",
                           lambda l, c: l["routes"][0].update(gate_id="gate_nope"))

    def test_narrow_lane_must_be_declared_and_kept_out_of_scatter(self):
        self.assert_caught("必须显式标 narrow_small_ball_lane",
                           lambda l, c: l["chokepoints"][0].update(clear_width_m=1.4))

        def inside_scatter(layout, catalog):
            for choke in layout["chokepoints"]:
                if choke["id"] == "cabinet_gap_north":
                    choke["position"] = [0, 0, 14]      # blocks_north 内部
        self.assert_caught("窄道落在散布区", inside_scatter)

    # ---- 道具 / 预算 / 场外物 ----
    def test_powerup_type_and_zone(self):
        self.assert_caught("未知道具类型",
                           lambda l, c: l["powerups"]["points"][0].update(type="timer"))
        self.assert_caught("未知区域",
                           lambda l, c: l["powerups"]["points"][0].update(zone_id="nowhere"))

    def test_budget_must_match_catalog(self):
        self.assert_caught("performance_budget.absorbable_instances",
                           lambda l, c: l["performance_budget"].update(absorbable_instances=1))

    def test_absorbable_structure_outside_bounds_is_an_error(self):
        """旧校验器靠 PicnicLawn 专用白名单挡这一类；新版换成地图无关的规则。"""
        def move_out(layout, catalog):
            # 把一个可卷物件的定义挂到「无归属 + 场外」的结构上：旧白名单时代这是漏网的
            for structure in layout["structures"]:
                if structure["definition_id"] == "powerup_pedestal":
                    structure.update(definition_id="marble", zone_id=None,
                                     position=[0, 0.4, 26])
                    break
        self.assert_caught("可卷物件在边界外", move_out)


class TraversabilityTests(unittest.TestCase):
    """立面分类（可走坡 / 爬升台阶 / 障碍）：三类各用各的判据，且都有负例。

    背景：`scale.max_traversable_slope_percent = 12` 曾被读成「全图每个面都 ≤12%」，
    于是花圃的 0.26 m 台阶（0.9 m 斜面 = 28.9%）被判成缺陷——可它的可行与否取决于
    球径（0.95 m 挡、1.10 m 过），不是坡度。这三条规则就是把这个区别写进数据。
    """

    def setUp(self):
        self.layout, self.catalog = load(MAPS_ROOT / "PicnicLawn")

    def run_with(self, mutate):
        layout = copy.deepcopy(self.layout)
        catalog = copy.deepcopy(self.catalog)
        mutate(layout, catalog)
        return vm.validate(layout, catalog)

    def assert_caught(self, fragment, mutate):
        report = self.run_with(mutate)
        self.assertTrue(any(fragment in error for error in report.errors),
                        f"expected {fragment!r}, got {report.errors}")
        return report

    @staticmethod
    def surface(layout, definition_id, index=0):
        hits = [s for s in layout["structures"] if s["definition_id"] == definition_id]
        assert hits, f"no structure with definition_id={definition_id}"
        return hits[index]

    # ---- 数据本身：分类齐全、报告能读出 28.9% ----
    def test_picniclawn_declares_every_rise(self):
        report = self.run_with(lambda layout, catalog: None)
        self.assertEqual(report.errors, [])
        self.assertEqual(report.warnings, [])
        steps = [row for row in report.surfaces if row["class"] == "climb_step"]
        self.assertEqual(len(steps), 4, "四个花圃台面都要标成爬升台阶")
        for row in steps:
            self.assertAlmostEqual(row["rise_m"], 0.26, places=3)
            self.assertAlmostEqual(row["slope_percent"], 28.9, places=1,
                                   msg="0.26 ÷ 0.9 = 28.9%，这个数字必须由数据算出来")
        grades = {row["label"].split("#")[0]: row for row in report.surfaces
                  if row["class"] == "drivable_grade"}
        self.assertAlmostEqual(grades["picnic_blanket"]["slope_percent"], 7.5, places=2)
        self.assertAlmostEqual(grades["tree_root_mound"]["slope_percent"], 11.2, places=2)
        self.assertEqual(sum(report.obstacles.values()), 38)

    # ---- 规则 (a)：可走坡必须由 rise ÷ run 算出来，且 ≤ 上限 ----
    def test_rule_a_too_steep_grade_is_an_error(self):
        def steepen(layout, catalog):
            self.surface(layout, "picnic_blanket")["traversability"]["run_m"] = 0.30
        report = self.assert_caught("超过 scale.max_traversable_slope_percent", steepen)
        self.assertTrue(any("20.0%" in error for error in report.errors),
                        f"0.06 ÷ 0.30 = 20.0%，要报出算出来的数：{report.errors}")

    def test_rule_a_declared_slope_cannot_lie(self):
        """声明一个好看的 slope_percent 不能替代几何：算出来 7.5% 就必须是 7.5%。"""
        def lie(layout, catalog):
            self.surface(layout, "tree_root_mound")["traversability"]["slope_percent"] = 5.0
        self.assert_caught("与几何算出的", lie)

    def test_rule_a_run_is_required(self):
        def drop_run(layout, catalog):
            del self.surface(layout, "picnic_blanket")["traversability"]["run_m"]
        self.assert_caught("必须写 run_m", drop_run)

    # ---- 规则 (b)：台阶高必须与尺寸门阈值相容 ----
    def test_rule_b_step_taller_than_the_gate_is_an_error(self):
        def too_tall(layout, catalog):
            bed = self.surface(layout, "flower_bed_frame")
            bed["size_m"][1] = 3.0          # 几何高 3 m：门说 1.10 m 能翻，纯属数据错误
            bed["position"][1] = 1.5
            bed["traversability"]["step_height_m"] = 3.0
        report = self.assert_caught("高于", too_tall)
        self.assertTrue(any("可翻越上限" in error and "几何却上不去" in error
                            for error in report.errors), report.errors)

    def test_rule_b_step_declared_height_must_match_geometry(self):
        def inflate(layout, catalog):
            self.surface(layout, "flower_bed_frame")["traversability"]["step_height_m"] = 0.40
        self.assert_caught("与几何算出的立面高度", inflate)

    def test_rule_b_lowered_platform_breaks_the_gate(self):
        """Lead 已否决的「台面降到 0.18 m」降级方案：0.18 ≤ 0.25×0.95，门就挡不住了。

        这条用例把那个决定锁进回归：谁再写「降到 0.18 m」，校验器直接报错。
        """
        def lower(layout, catalog):
            for bed in layout["structures"]:
                if bed["definition_id"] == "flower_bed_frame":
                    bed["size_m"][1] = 0.18
                    bed["position"][1] = 0.09
                    bed["traversability"]["step_height_m"] = 0.18
        report = self.assert_caught("挡不住", lower)
        self.assertTrue(any("落在 0.95 m 球的可翻越上限" in error for error in report.errors),
                        report.errors)

    # ---- 规则 (c)：不许有未分类的高差 ----
    def test_rule_c_unclassified_rise_is_an_error(self):
        def unclassify(layout, catalog):
            del self.surface(layout, "core_hedge")["traversability"]
        self.assert_caught("没声明 traversability", unclassify)

    def test_rule_c_low_decal_does_not_need_classification(self):
        """出生地贴 0.015 m、交分盘 0.02 m：低于 max_route_step_m，不算立面。"""
        def unclassify_pads(layout, catalog):
            for structure in layout["structures"]:
                if structure["definition_id"] in ("bank_pad", "spawn_marker",
                                                  "ground_terrain", "path_mesh"):
                    structure.pop("traversability", None)
        report = self.run_with(unclassify_pads)
        self.assertEqual(report.errors, [])
        self.assertEqual(report.warnings, [])

    # ---- 类别与门的矛盾 ----
    def test_climb_gate_cannot_point_at_a_non_step(self):
        def as_obstacle(layout, catalog):
            self.surface(layout, "flower_bed_frame")["traversability"] = {"class": "obstacle"}
        self.assert_caught("被标成 obstacle", as_obstacle)

    def test_grade_must_be_fixed_terrain(self):
        def absorbable_blanket(layout, catalog):
            for row in catalog["rows"]:
                if row["id"] == "picnic_blanket":
                    row["role"] = "absorbable"
        self.assert_caught("必须是固定地形", absorbable_blanket)

    def test_obstacle_with_slope_numbers_is_contradictory(self):
        def hedge_slope(layout, catalog):
            self.surface(layout, "boundary_hedge")["traversability"] = {
                "class": "obstacle", "run_m": 1.0}
        self.assert_caught("不该声明 run_m", hedge_slope)

    # ---- v1 平滑：旧图不被迫升级 ----
    def test_v1_map_derives_steps_from_climb_gates(self):
        layout, catalog = load(MAPS_ROOT / "ToyRoom")
        report = vm.validate(layout, catalog)
        self.assertEqual(layout["schema_version"], 1)
        self.assertEqual(report.errors, [])
        self.assertEqual(report.warnings, [])
        steps = [row for row in report.surfaces if row["class"] == "climb_step"]
        self.assertEqual(len(steps), 4, "ToyRoom 的 4 块拼图地垫由 climb 门推出")
        for row in steps:
            self.assertAlmostEqual(row["rise_m"], 0.26, places=3)
            self.assertIn("台阶 0.95→1.1 m", row["verdict"])
        self.assertTrue(any("schema_version=1" in note for note in report.notes))

    # ---- 输出契约 ----
    def test_cli_prints_the_slope_table_and_json_carries_it(self):
        result = run_cli(MAPS_ROOT / "PicnicLawn")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("立面审计", result.stdout)
        self.assertIn("28.9%", result.stdout)
        summary = json.loads(run_cli(MAPS_ROOT / "PicnicLawn", "--json").stdout)
        self.assertEqual(summary["status"], "OK")
        steps = [row for row in summary["surfaces"] if row["class"] == "climb_step"]
        self.assertEqual(len(steps), 4)
        self.assertEqual(steps[0]["slope_percent"], 28.89)
        self.assertEqual(summary["obstacles"]["core_hedge"], 8)


class RetirementTests(unittest.TestCase):
    """退役本身也要有测试：不许再出现「每图一份」的分叉实现。"""

    def test_no_per_map_validator_files(self):
        leftovers = sorted(str(path.relative_to(REPO_ROOT))
                           for path in MAPS_ROOT.glob("*/validate_*.py"))
        self.assertEqual(leftovers, [], f"地图目录不应再有专用校验器：{leftovers}")

    def test_validator_module_is_importable_without_side_effects(self):
        self.assertTrue(callable(vm.validate))
        self.assertTrue(callable(vm.main))


if __name__ == "__main__":
    unittest.main()
