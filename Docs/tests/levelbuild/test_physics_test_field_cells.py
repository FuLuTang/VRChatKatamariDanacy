"""The physics test field's data contract: four cells, their placements and their criteria.

Run from the repository root:

    python3 -m unittest discover -s Docs/tests -p 'test_*.py'

What this file is for. The field's geometry moved out of
``Docs/Design/BallMotor/physics_test_field.json`` into one ``placement.json`` per cell
(``Docs/Design/Levels/physics_test_field/<cell>/``), and the runner's plan stayed in the
JSON. Two files per cell can drift apart silently, and the Udon runtime cannot read either
of them at run time - only the baked serialized arrays - so the only cheap place to catch
drift is here:

* the placement must pass ``levelbuild check`` (capacity, ground, budgets);
* the cell's geometry must sit inside the cell's own activation rect, and the ball must
  start inside it too (a rect that is a lie would move the wrong cell on);
* ``nominal_m`` must equal the cell's deck height, because every deck/step criterion is
  written against it;
* every metric a wired check names must be one the driver can actually read
  (``TryReadMetric``), which is checked by reading the C# source rather than keeping a
  second list of metric names in Python.
"""

import json
import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import placement_core as core  # noqa: E402

FIELD_JSON = ROOT / "Docs/Design/BallMotor/physics_test_field.json"
LEVELS_DIR = ROOT / "Docs/Design/Levels/physics_test_field"
DRIVER_CS = (ROOT / "VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/PhysicsTestField/"
                    "PhysicsTestFieldDriver.cs")
CELL_CS = (ROOT / "VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/PhysicsTestField/"
                  "PhysicsTestCell.cs")
SETTER_CS = (ROOT / "VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Labs/PhysicsTestField/"
                    "LabBallSizeSetter.cs")
CRITERIA_CS = (ROOT / "VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/PhysicsTestField/"
                      "PhysicsTestFieldCriteriaImporter.cs")

OPS = ("abs<=", "<=", ">=", "==", "abs>=")
GATES = (None, "P1", "P2", "P3")
READOUTS = ("frame", "trace", "counter")


def load_field():
    return json.loads(FIELD_JSON.read_text(encoding="utf-8"))


def load_cell(placement_rel):
    """The cell's placement document, or None when the file is missing."""
    path = ROOT / "Docs/Design/Levels" / placement_rel
    if not path.is_file():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def driver_metrics():
    """Metric names ``PhysicsTestFieldDriver.TryReadMetric`` can answer."""
    source = DRIVER_CS.read_text(encoding="utf-8")
    return set(re.findall(r'Same\(metric,\s*"([a-z0-9_]+)"\)', source))


def structure_aabb(structure):
    center = structure["center"]
    size = structure["size"]
    quat = structure["rotationQuat"]
    half = core.world_half_extents(size, quat)
    return ((center[0] - half[0], center[1] - half[1], center[2] - half[2]),
            (center[0] + half[0], center[1] + half[1], center[2] + half[2]))


class FieldJsonShape(unittest.TestCase):
    def setUp(self):
        self.doc = load_field()

    def test_schema_version_and_identity(self):
        self.assertEqual(self.doc["schema_version"], 2)
        self.assertEqual(self.doc["id"], "physics_test_field")
        self.assertIn("PHYSICS_TEST_FIELD.md", self.doc["design_doc"])

    def test_geometry_is_not_duplicated_in_the_criteria_file(self):
        # The whole point of the migration: no registration blocks, no box poses, no ramps.
        for cell in self.doc["cells"]:
            self.assertNotIn("registration", cell, cell["id"])
            self.assertNotIn("props", cell, cell["id"])
            self.assertIn("placement", cell, cell["id"])

    def test_every_wired_cell_has_exactly_one_placement(self):
        wired = [cell for cell in self.doc["cells"] if cell.get("wired")]
        self.assertTrue(wired, "no wired cells")
        for cell in wired:
            placement = cell["placement"]
            self.assertTrue(placement.startswith("physics_test_field/"), placement)
            self.assertEqual(Path(placement).name, "placement.json")

    def test_cell_ids_are_unique_and_cover_the_planned_ones(self):
        ids = [cell["id"] for cell in self.doc["cells"]]
        planned = [cell["id"] for cell in self.doc["planned_cells"]]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertEqual(len(planned), len(set(planned)))
        self.assertFalse(set(ids) & set(planned), "a cell is both wired and planned")
        self.assertEqual(len(ids) + len(planned), 21)

    def test_planned_cells_carry_no_geometry(self):
        for cell in self.doc["planned_cells"]:
            self.assertFalse(cell.get("wired"), cell["id"])
            self.assertIsNone(cell.get("placement"), cell["id"])
            self.assertNotIn("registration", cell, cell["id"])

    def test_expect_rows_follow_the_diameter_presets(self):
        presets = len(self.doc["diameters"])
        self.assertEqual(presets, 4)
        for cell in self.doc["cells"]:
            for check in cell["checks"]:
                self.assertEqual(len(check["expect"]), presets,
                                 "%s/%s" % (cell["id"], check["id"]))
                for value in check["expect"]:
                    self.assertIn(value, ("pass", "fail", "n/a"))

    def test_check_vocabulary_is_closed(self):
        for cell in self.doc["cells"]:
            for check in cell["checks"]:
                self.assertIn(check["op"], OPS, check["id"])
                self.assertIn(check.get("gate"), GATES, check["id"])
                self.assertIn(check["readout"], READOUTS, check["id"])

    def test_driver_and_importer_files_exist(self):
        for path in (DRIVER_CS, CELL_CS, SETTER_CS, CRITERIA_CS):
            self.assertTrue(path.is_file(), path)

    def test_kind_whitelist_contains_the_feeder_kind(self):
        whitelist = self.doc["driver"]["kind_whitelist"]
        self.assertIn("lab-feeder", whitelist)

    def test_capacity_budget_matches_the_motor(self):
        capacities = self.doc["motor_constants"]["capacities"]
        self.assertEqual(capacities["planes"], 8)
        self.assertEqual(capacities["static_boxes"], 8)
        self.assertEqual(capacities["ramps"], 4)


class CellPlacements(unittest.TestCase):
    def setUp(self):
        self.field = load_field()
        self.cells = [cell for cell in self.field["cells"] if cell.get("wired")]

    def test_every_wired_placement_exists_and_passes_the_gate(self):
        for cell in self.cells:
            path = ROOT / "Docs/Design/Levels" / cell["placement"]
            self.assertTrue(path.is_file(), path)
            report = core.validate(core.load_doc(path), path=path)
            self.assertTrue(report.ok, "%s: %s" % (path, report.errors))
            self.assertEqual(report.warnings, [], "%s: %s" % (path, report.warnings))

    def test_arena_id_matches_the_cell_and_its_folder(self):
        for cell in self.cells:
            doc = load_cell(cell["placement"])
            self.assertEqual(doc["level"]["arena"]["id"], cell["id"])
            self.assertEqual(Path(cell["placement"]).parent.name, cell["id"])

    def test_every_cell_is_a_complete_arena(self):
        # ClearObstacles zeroes the motor's global plane table, so a cell that borrowed the
        # field's walls from another host would leave the ball with no boundary at all.
        for cell in self.cells:
            doc = load_cell(cell["placement"])
            roles = [structure["role"] for structure in doc["structures"]]
            self.assertEqual(roles.count("ground"), 1, cell["id"])
            self.assertEqual(roles.count("boundary"), 4, cell["id"])
            self.assertEqual(roles.count("box"), 1, cell["id"])
            self.assertEqual(doc["props"], [], cell["id"])

    def test_the_shared_base_is_identical_in_every_cell(self):
        bases = {}
        for cell in self.cells:
            doc = load_cell(cell["placement"])
            base = [structure for structure in doc["structures"]
                    if structure["role"] in ("ground", "boundary")]
            bases[cell["id"]] = json.dumps(sorted(base, key=lambda item: item["id"]),
                                           sort_keys=True)
        distinct = set(bases.values())
        self.assertEqual(len(distinct), 1,
                         "the field floor / walls differ between cells: %s" % sorted(bases))

    def test_nominal_is_the_deck_height(self):
        for cell in self.cells:
            doc = load_cell(cell["placement"])
            deck = [structure for structure in doc["structures"] if structure["role"] == "box"][0]
            (_, bottom, _), (_, top, _) = structure_aabb(deck)
            self.assertAlmostEqual(bottom, 0.0, places=6, msg=cell["id"])
            self.assertAlmostEqual(cell["nominal_m"], top, places=6, msg=cell["id"])

    def test_geometry_stays_inside_its_own_footprint(self):
        for cell in self.cells:
            doc = load_cell(cell["placement"])
            footprint = cell["footprint"]
            center = footprint["center"]
            size = footprint["size"]
            min_x, max_x = center[0] - size[0] / 2.0, center[0] + size[0] / 2.0
            min_z, max_z = center[1] - size[1] / 2.0, center[1] + size[1] / 2.0
            for structure in doc["structures"]:
                if structure["role"] not in ("box", "ramp"):
                    continue
                (low, high) = structure_aabb(structure)
                self.assertGreaterEqual(low[0], min_x - 1e-6, "%s/%s" % (cell["id"], structure["id"]))
                self.assertLessEqual(high[0], max_x + 1e-6, "%s/%s" % (cell["id"], structure["id"]))
                self.assertGreaterEqual(low[2], min_z - 1e-6, "%s/%s" % (cell["id"], structure["id"]))
                self.assertLessEqual(high[2], max_z + 1e-6, "%s/%s" % (cell["id"], structure["id"]))

    def test_the_ball_starts_inside_its_footprint(self):
        for cell in self.cells:
            center = cell["footprint"]["center"]
            size = cell["footprint"]["size"]
            pose = cell["start_pose"]["center"]
            self.assertGreaterEqual(pose[0], center[0] - size[0] / 2.0, cell["id"])
            self.assertLessEqual(pose[0], center[0] + size[0] / 2.0, cell["id"])
            self.assertGreaterEqual(pose[2], center[1] - size[1] / 2.0, cell["id"])
            self.assertLessEqual(pose[2], center[1] + size[1] / 2.0, cell["id"])
            self.assertAlmostEqual(pose[1], 0.15, places=6, msg=cell["id"])

    def test_the_start_is_outside_the_deck_so_the_run_can_accelerate(self):
        # A cell whose ball starts on top of its own deck measures the deck, not the climb.
        for cell in self.cells:
            doc = load_cell(cell["placement"])
            deck = [structure for structure in doc["structures"] if structure["role"] == "box"][0]
            (low, high) = structure_aabb(deck)
            pose = cell["start_pose"]["center"]
            inside_x = low[0] <= pose[0] <= high[0]
            inside_z = low[2] <= pose[2] <= high[2]
            self.assertFalse(inside_x and inside_z, cell["id"])

    def test_nested_level_ids_resolve_to_the_file(self):
        for cell in self.cells:
            path = core.placement_path(cell["placement"][: -len("/placement.json")])
            self.assertTrue(path.is_file(), path)


class CellCriteria(unittest.TestCase):
    def setUp(self):
        self.field = load_field()
        self.cells = [cell for cell in self.field["cells"] if cell.get("wired")]
        self.metrics = driver_metrics()

    def test_metric_names_are_implemented_by_the_driver(self):
        self.assertTrue(self.metrics, "no metrics parsed from the driver")
        for cell in self.cells:
            for check in cell["checks"]:
                if check["readout"] != "frame":
                    continue
                self.assertIn(check["metric"], self.metrics,
                              "%s/%s names a metric the driver cannot read" %
                              (cell["id"], check["id"]))

    def test_a_cell_has_at_least_one_measured_check(self):
        for cell in self.cells:
            measured = [check for check in cell["checks"] if check["readout"] == "frame"]
            self.assertTrue(measured, cell["id"])

    def test_drive_timeline_starts_at_zero_and_increases(self):
        for cell in self.cells:
            drive = cell["drive"]
            self.assertEqual(drive[0]["t"], 0.0, cell["id"])
            times = [row["t"] for row in drive]
            self.assertEqual(times, sorted(times), cell["id"])
            self.assertGreater(cell["stop_seconds"], 0.0, cell["id"])

    def test_every_expectation_is_reachable(self):
        # Every check must expect *something* at at least one diameter, otherwise it is dead
        # weight that can never go red.
        for cell in self.cells:
            for check in cell["checks"]:
                self.assertIn("pass", check["expect"], "%s/%s" % (cell["id"], check["id"]))

    def test_boundary_cells_state_the_equality(self):
        # C09 is the <= vs < case: rise == stepLimit(0.30) == 0.075 must expect a climb.
        c09 = [cell for cell in self.cells if cell["id"] == "C09"][0]
        self.assertAlmostEqual(c09["nominal_m"], 0.075, places=6)
        on_step = [check for check in c09["checks"] if check["id"] == "on_step"][0]
        self.assertEqual(on_step["expect"][0], "pass")
        # C10 is the wall case at 0.30 and climbable from 0.60.
        c10 = [cell for cell in self.cells if cell["id"] == "C10"][0]
        wall = [check for check in c10["checks"] if check["id"] == "on_step"][0]
        self.assertEqual(wall["expect"], ["fail", "pass", "pass", "pass"])


class ArenaRules(unittest.TestCase):
    """The new placement rules a cell document has to satisfy."""

    def setUp(self):
        self.path = LEVELS_DIR / "C09/placement.json"
        self.doc = core.load_doc(self.path)

    def mutate(self):
        import copy
        return copy.deepcopy(self.doc)

    def test_copied_file_without_renamed_arena_is_refused(self):
        report = core.validate(self.mutate(), path=LEVELS_DIR / "C14/placement.json")
        self.assertFalse(report.ok)
        self.assertTrue(any("arena.id is" in error for error in report.errors), report.errors)

    def test_arena_needs_an_id(self):
        doc = self.mutate()
        doc["level"]["arena"] = {}
        report = core.validate(doc)
        self.assertFalse(report.ok)
        self.assertTrue(any("arena.id is required" in error for error in report.errors))

    def test_geometry_outside_the_field_bounds_is_refused(self):
        doc = self.mutate()
        for structure in doc["structures"]:
            if structure["role"] == "box":
                structure["center"][0] = 60.0
        report = core.validate(doc)
        self.assertFalse(report.ok)
        self.assertTrue(any("level.bounds" in error for error in report.errors), report.errors)

    def test_boundaries_are_exempt_from_the_bounds_rule(self):
        # The walls sit ON the field edge; the rule is about boxes and ramps.
        doc = self.mutate()
        report = core.validate(doc)
        self.assertTrue(report.ok, report.errors)

    def test_nested_level_id_is_accepted_and_escapes_are_not(self):
        self.assertTrue(core.is_valid_level_id("physics_test_field"))
        self.assertTrue(core.is_valid_level_id("physics_test_field/C14"))
        self.assertFalse(core.is_valid_level_id("../C14"))
        self.assertFalse(core.is_valid_level_id("a/b/c"))
        self.assertFalse(core.is_valid_level_id("physics_test_field/"))


if __name__ == "__main__":
    unittest.main()
