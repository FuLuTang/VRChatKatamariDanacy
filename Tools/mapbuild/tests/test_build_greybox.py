#!/usr/bin/env python3
"""build_greybox.py 的单元测试（unittest，无第三方依赖）。

运行：
    python3 -m unittest discover -s Tools/mapbuild/tests
    python3 -m unittest Tools.mapbuild.tests.test_build_greybox -v   # 若把 Tools 当包
"""

import csv
import json
import math
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent
TOOL_DIR = TESTS_DIR.parent
REPO_ROOT = TOOL_DIR.parents[1]
sys.path.insert(0, str(TOOL_DIR))

import build_greybox as bg  # noqa: E402  （必须在 sys.path 调整之后导入）

MAP_DIR = REPO_ROOT / "Docs/Design/Maps/PicnicLawn"
LAYOUT = MAP_DIR / "layout.json"
CATALOG = MAP_DIR / "prop_catalog.csv"
COMMITTED = MAP_DIR / "unity_greybox.json"
SCRIPT = TOOL_DIR / "build_greybox.py"


def load_payload() -> dict:
    return json.loads(bg.build())


def run_tool(*args, cwd=REPO_ROOT):
    return subprocess.run([sys.executable, str(SCRIPT), *map(str, args)],
                          cwd=str(cwd), capture_output=True, text=True)


class SchemaShapeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.payload = load_payload()

    def test_schema_version_and_top_level_keys(self):
        self.assertEqual(self.payload["schema_version"], 1)
        self.assertEqual(tuple(self.payload), bg.TOP_LEVEL_KEYS)
        layout = json.loads(LAYOUT.read_text(encoding="utf-8"))
        self.assertEqual(self.payload["map_id"], layout["map_id"])
        self.assertEqual(self.payload["status"], layout["status"])

    def test_every_collection_is_present_and_non_empty(self):
        for key in bg.TOP_LEVEL_KEYS:
            value = self.payload[key]
            if isinstance(value, list):
                self.assertTrue(value, f"{key} 不能为空")
        counts = {key: len(value) for key, value in self.payload.items()
                  if isinstance(value, list)}
        self.assertEqual(counts["zones"], 21)
        self.assertEqual(counts["structures"], 60)
        self.assertEqual(counts["spawns"], 4)
        self.assertEqual(counts["bank_anchors"], 4)
        self.assertEqual(counts["size_gates"], 8)
        self.assertEqual(counts["scatter_regions"], 20)
        self.assertEqual(counts["routes"], 21)
        self.assertEqual(counts["chokepoints"], 16)
        self.assertEqual(counts["props"], 54)

    def test_bounds_and_scale_are_flat_scalar_records(self):
        for record_name in ("bounds", "scale", "bank_rotation"):
            record = self.payload[record_name]
            self.assertIsInstance(record, dict, record_name)
            for key, value in record.items():
                if isinstance(value, list):
                    self.assertTrue(all(isinstance(v, (str, int, float)) for v in value),
                                    f"{record_name}.{key} 只能是标量数组")
                else:
                    self.assertIsInstance(value, (str, int, float),
                                          f"{record_name}.{key} 必须是标量")

    def test_no_nested_objects_beyond_one_level(self):
        """顶层 → 标量/标量数组/记录/记录数组；记录内只能是标量或标量数组。

        这就是 JsonUtility 能吃的形状：数组元素可以是 [Serializable] 类，
        但类里不能再放类，也不能放“数组的数组”。
        """
        scalar = (str, int, float, bool)

        def assert_flat_record(record, where):
            self.assertIsInstance(record, dict, where)
            for key, value in record.items():
                self.assertNotIsInstance(value, dict, f"{where}.{key}: 记录里不能再嵌对象")
                if isinstance(value, list):
                    self.assertTrue(all(isinstance(item, scalar) for item in value),
                                    f"{where}.{key}: 只能是标量数组，不能是"
                                    f"{type(value[0]).__name__} 数组" if value else "")
                else:
                    self.assertIsInstance(value, scalar,
                                          f"{where}.{key}: 必须是标量（{type(value).__name__}）")

        for key, value in self.payload.items():
            if isinstance(value, dict):
                assert_flat_record(value, key)
            elif isinstance(value, list):
                if value and isinstance(value[0], dict):
                    self.assertTrue(all(isinstance(item, dict) for item in value),
                                    f"{key}: 不能混装标量与记录")
                    for index, item in enumerate(value):
                        assert_flat_record(item, f"{key}[{index}]")
                else:
                    self.assertTrue(all(isinstance(item, scalar) for item in value),
                                    f"{key}: 只能是标量或记录数组")
            else:
                self.assertIsInstance(value, scalar, f"{key}: 顶层值必须是标量/记录/数组")

    def test_no_arrays_of_arrays(self):
        def scan(node, path):
            if isinstance(node, dict):
                for key, value in node.items():
                    scan(value, f"{path}.{key}")
            elif isinstance(node, list):
                for index, item in enumerate(node):
                    self.assertNotIsInstance(item, list, f"{path}[{index}]: 不允许数组套数组")
                    scan(item, f"{path}[{index}]")

        scan(self.payload, "payload")

    def test_no_nulls_anywhere(self):
        def scan(node, path):
            if node is None:
                self.fail(f"{path}: 不允许 null（可选引用写成空字符串）")
            if isinstance(node, dict):
                for key, value in node.items():
                    scan(value, f"{path}.{key}")
            elif isinstance(node, list):
                for index, value in enumerate(node):
                    scan(value, f"{path}[{index}]")

        scan(self.payload, "payload")

    def test_flags_are_ints_not_bools(self):
        for route in self.payload["routes"]:
            self.assertIn(route["is_closed"], (0, 1))
            self.assertIsInstance(route["is_closed"], int)
        self.assertIn(self.payload["bank_rotation"]["occupancy_lock"], (0, 1))
        for chokepoint in self.payload["chokepoints"]:
            self.assertIn(chokepoint["narrow_small_ball_lane"], (0, 1))

    def test_parallel_arrays_are_aligned(self):
        for route in self.payload["routes"]:
            self.assertEqual(len(route["waypoints_x"]), route["waypoint_count"],
                             route["id"])
            self.assertEqual(len(route["waypoints_z"]), route["waypoint_count"],
                             route["id"])
        for region in self.payload["scatter_regions"]:
            n = region["prop_definition_count"]
            self.assertEqual(len(region["prop_ids"]), n, region["zone_id"])
            self.assertEqual(len(region["prop_counts_min"]), n, region["zone_id"])
            self.assertEqual(len(region["prop_counts_max"]), n, region["zone_id"])
            self.assertEqual(sum(region["prop_counts_min"]), region["count_min_total"])
        scale = self.payload["scale"]
        self.assertEqual(len(scale["ball_band_ids"]), scale["ball_band_count"])
        self.assertEqual(len(scale["ball_band_min_m"]), scale["ball_band_count"])
        self.assertEqual(len(scale["pickup_tier_ids"]), len(scale["pickup_diameter_min_m"]))


class DeterminismTests(unittest.TestCase):
    def test_build_is_byte_identical_across_runs(self):
        self.assertEqual(bg.build(), bg.build())

    def test_rendered_text_is_canonical(self):
        text = bg.build()
        self.assertTrue(text.endswith("\n"))
        self.assertFalse(text.endswith("\n\n"))
        self.assertTrue(text.startswith('{\n  "schema_version": 1,\n'))
        self.assertNotIn("\t", text)
        self.assertNotIn("\r", text)
        self.assertNotIn("\\u", text)  # 中文不转义

    def test_record_order_is_sorted_by_id(self):
        payload = load_payload()
        for key in ("zones", "spawns", "bank_anchors", "size_gates", "routes",
                    "chokepoints", "props"):
            ids = [record["id"] for record in payload[key]]
            self.assertEqual(ids, sorted(ids), f"{key} 必须按 id 排序")
        zone_ids = [region["zone_id"] for region in payload["scatter_regions"]]
        self.assertEqual(zone_ids, sorted(zone_ids))
        indexes = [record["index"] for record in payload["structures"]]
        self.assertEqual(indexes, list(range(len(indexes))))

    def test_committed_file_is_up_to_date(self):
        self.assertTrue(COMMITTED.exists(), "unity_greybox.json 必须提交")
        committed = COMMITTED.read_text(encoding="utf-8")
        self.assertEqual(committed, bg.build(),
                         "已提交的 unity_greybox.json 与作者数据不一致；运行 build_greybox.py")


class CrossReferenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.payload = load_payload()
        cls.zones = {zone["id"] for zone in cls.payload["zones"]}
        cls.props = {prop["id"]: prop for prop in cls.payload["props"]}
        cls.routes = {route["id"] for route in cls.payload["routes"]}
        cls.gates = {gate["id"] for gate in cls.payload["size_gates"]}
        cls.spawns = {spawn["id"] for spawn in cls.payload["spawns"]}
        cls.anchors = {anchor["id"] for anchor in cls.payload["bank_anchors"]}

    def test_structure_zone_ids_resolve(self):
        for structure in self.payload["structures"]:
            zone_id = structure["zone_id"]
            if zone_id:
                self.assertIn(zone_id, self.zones,
                              f"structures[{structure['index']}] {structure['definition_id']}")
            self.assertIn(structure["definition_id"], self.props)

    def test_spawn_zone_ids_resolve(self):
        for spawn in self.payload["spawns"]:
            self.assertIn(spawn["zone_id"], self.zones, spawn["id"])

    def test_scatter_references_resolve(self):
        seen_zones = set()
        for region in self.payload["scatter_regions"]:
            self.assertIn(region["zone_id"], self.zones, region["zone_id"])
            self.assertNotIn(region["zone_id"], seen_zones, "每个区域只能有一条散布记录")
            seen_zones.add(region["zone_id"])
            for prop_id in region["prop_ids"]:
                self.assertIn(prop_id, self.props, region["zone_id"])

    def test_gate_references_resolve(self):
        for gate in self.payload["size_gates"]:
            self.assertIn(gate["definition_id"], self.props, gate["id"])
            self.assertIn(gate["type"], ("absorb", "climb"), gate["id"])
            opens = gate["opens_path"]
            self.assertTrue(opens in self.routes or opens in self.zones,
                            f"{gate['id']}: opens_path {opens!r} 不存在")
            self.assertLess(gate["blocks_ball_diameter_below_m"],
                            gate["absorbable_at_ball_diameter_m"], gate["id"])

    def test_route_references_resolve(self):
        for route in self.payload["routes"]:
            if route["spawn_id"]:
                self.assertIn(route["spawn_id"], self.spawns, route["id"])
            if route["bank_anchor_id"]:
                self.assertIn(route["bank_anchor_id"], self.anchors, route["id"])
            if route["gate_id"]:
                self.assertIn(route["gate_id"], self.gates, route["id"])
            for z in route["waypoints_z"]:
                self.assertLessEqual(abs(z), 20.0 + 1e-9, route["id"])

    def test_chokepoint_gate_references_resolve(self):
        for chokepoint in self.payload["chokepoints"]:
            if chokepoint["gate_id"]:
                self.assertIn(chokepoint["gate_id"], self.gates, chokepoint["id"])

    def test_bank_anchor_references_resolve(self):
        for anchor in self.payload["bank_anchors"]:
            self.assertIn(anchor["zone_id"], self.zones, anchor["id"])
            for route_id in anchor["entrance_route_ids"] + anchor["approach_route_ids"]:
                self.assertIn(route_id, self.routes, anchor["id"])
        rotation = self.payload["bank_rotation"]
        self.assertEqual(sorted(rotation["order"]), sorted(self.anchors))
        self.assertEqual(len(rotation["activate_at_s"]), len(rotation["order"]))

    def test_prop_zone_ids_resolve(self):
        for prop_id, prop in self.props.items():
            for zone_id in prop["zone_ids"]:
                self.assertIn(zone_id, self.zones, prop_id)


class CountConsistencyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.payload = load_payload()
        with CATALOG.open(encoding="utf-8", newline="") as handle:
            cls.catalog = {row["id"]: row for row in csv.DictReader(handle)}

    def test_scatter_plus_absorb_gates_equal_catalog_counts(self):
        scatter_use = {}
        for region in self.payload["scatter_regions"]:
            for prop_id, count in zip(region["prop_ids"], region["prop_counts_min"]):
                scatter_use[prop_id] = scatter_use.get(prop_id, 0) + count
        gate_use = {}
        for gate in self.payload["size_gates"]:
            if gate["type"] == "absorb":
                gate_use[gate["definition_id"]] = gate_use.get(gate["definition_id"], 0) + 1

        for prop in self.payload["props"]:
            prop_id = prop["id"]
            expected = (scatter_use.get(prop_id, 0) + gate_use.get(prop_id, 0)
                        if prop["role"] == "absorbable"
                        else sum(1 for structure in self.payload["structures"]
                                 if structure["definition_id"] == prop_id))
            self.assertEqual(prop["count"], expected, prop_id)
            self.assertEqual(prop["count"], int(self.catalog[prop_id]["count_in_map"]), prop_id)

    def test_totals_match_design_summary(self):
        absorbable = [p for p in self.payload["props"] if p["role"] == "absorbable"]
        fixed = [p for p in self.payload["props"] if p["role"] == "fixed"]
        self.assertEqual(len(absorbable), 37)
        self.assertEqual(len(fixed), 17)
        self.assertEqual(sum(p["count"] for p in absorbable), 452)
        self.assertEqual(sum(p["count"] for p in fixed), 60)
        self.assertEqual(len(self.payload["structures"]), 60)


class RoundingTests(unittest.TestCase):
    def test_all_floats_are_rounded_to_three_decimals(self):
        payload = load_payload()

        def scan(node, path):
            if isinstance(node, float):
                self.assertEqual(node, round(node, bg.PRECISION), f"{path}: {node!r}")
                self.assertFalse(node == 0.0 and math.copysign(1.0, node) < 0,
                                 f"{path}: 不允许 -0.0")
            elif isinstance(node, dict):
                for key, value in node.items():
                    scan(value, f"{path}.{key}")
            elif isinstance(node, list):
                for index, value in enumerate(node):
                    scan(value, f"{path}[{index}]")

        scan(payload, "payload")

    def test_known_values_survive_rounding(self):
        payload = load_payload()
        zones = {zone["id"]: zone for zone in payload["zones"]}
        self.assertAlmostEqual(zones["flower_bed_nw"]["min_z"], 6.1)
        self.assertAlmostEqual(zones["tree_roots_nw"]["area_m2"], 6.25)
        self.assertAlmostEqual(zones["blanket_west"]["max_x"], -7.0)
        gates = {gate["id"]: gate for gate in payload["size_gates"]}
        self.assertAlmostEqual(gates["gate_bench_nw"]["position_x"], -5.72)
        self.assertAlmostEqual(gates["gate_bench_nw"]["absorbable_at_ball_diameter_m"], 1.25)


class CliTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="mapbuild-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.layout = self.tmp / "layout.json"
        self.catalog = self.tmp / "prop_catalog.csv"
        self.out = self.tmp / "unity_greybox.json"
        shutil.copy2(LAYOUT, self.layout)
        shutil.copy2(CATALOG, self.catalog)

    def test_check_passes_on_fresh_output_then_detects_stale_copy(self):
        result = run_tool("--layout", self.layout, "--catalog", self.catalog,
                          "--out", self.out)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(self.out.exists())

        fresh = run_tool("--layout", self.layout, "--catalog", self.catalog,
                         "--out", self.out, "--check")
        self.assertEqual(fresh.returncode, 0, fresh.stderr)
        self.assertIn("OK", fresh.stdout)

        # 作者数据改一个字，输出就过期：--check 必须发现（且不动已提交文件）。
        layout = json.loads(self.layout.read_text(encoding="utf-8"))
        layout["zones"][0]["notes"] = layout["zones"][0]["notes"] + "（改）"
        self.layout.write_text(json.dumps(layout, ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8")
        before = self.out.read_bytes()

        stale = run_tool("--layout", self.layout, "--catalog", self.catalog,
                         "--out", self.out, "--check")
        self.assertEqual(stale.returncode, 1, stale.stdout)
        self.assertIn("stale", stale.stderr)
        self.assertIn("第一处不同", stale.stderr)
        self.assertEqual(self.out.read_bytes(), before, "--check 不能写文件")

    def test_check_reports_missing_output(self):
        result = run_tool("--layout", self.layout, "--catalog", self.catalog,
                          "--out", self.out, "--check")
        self.assertEqual(result.returncode, 1)
        self.assertIn("不存在", result.stderr)

    def test_unknown_zone_reference_names_the_offending_record(self):
        layout = json.loads(self.layout.read_text(encoding="utf-8"))
        layout["structures"][0]["zone_id"] = "no_such_zone"
        self.layout.write_text(json.dumps(layout, ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8")
        result = run_tool("--layout", self.layout, "--catalog", self.catalog,
                          "--out", self.out)
        self.assertEqual(result.returncode, 2, result.stdout)
        self.assertIn("structures[0]", result.stderr)
        self.assertIn("no_such_zone", result.stderr)
        self.assertFalse(self.out.exists(), "校验失败时不能写出半成品")

    def test_unknown_scatter_prop_names_the_offending_region(self):
        layout = json.loads(self.layout.read_text(encoding="utf-8"))
        layout["prop_scatter"][0]["definitions"][0]["id"] = "ghost_prop"
        self.layout.write_text(json.dumps(layout, ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8")
        result = run_tool("--layout", self.layout, "--catalog", self.catalog,
                          "--out", self.out)
        self.assertEqual(result.returncode, 2)
        self.assertIn("prop_scatter[", result.stderr)
        self.assertIn("ghost_prop", result.stderr)

    def test_count_mismatch_names_the_prop(self):
        layout = json.loads(self.layout.read_text(encoding="utf-8"))
        region = next(r for r in layout["prop_scatter"]
                      if any(d["id"] == "plate" for d in r["definitions"]))
        entry = next(d for d in region["definitions"] if d["id"] == "plate")
        entry["count_min"] -= 1
        entry["count_max"] = entry["count_min"]
        self.layout.write_text(json.dumps(layout, ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8")
        result = run_tool("--layout", self.layout, "--catalog", self.catalog,
                          "--out", self.out)
        self.assertEqual(result.returncode, 2)
        self.assertIn("plate", result.stderr)
        self.assertIn("count_in_map", result.stderr)

    def test_runs_from_any_working_directory(self):
        result = run_tool("--layout", self.layout, "--catalog", self.catalog,
                          "--out", self.out, cwd=self.tmp)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.out.read_text(encoding="utf-8"), bg.build())


if __name__ == "__main__":
    unittest.main()
