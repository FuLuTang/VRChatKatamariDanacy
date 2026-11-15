"""The physics test field's data contract: 9 cells, their placements and their criteria.

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
* a ramp cell must declare ``role=ramp`` (the motor must accept it) and a 50-degree
  adversarial cell must declare a ``kind=ramp_probe`` box instead, because the authoring
  gate refuses over-45 ramps (it asks for a box) and the motor's refusal getter can only
  be exercised through a probe;
* every metric a wired check names must be one the driver can actually read
  (``TryReadMetric``), which is checked by reading the C# source rather than keeping a
  second list of metric names in Python.
"""

import json
import math
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
MOTOR_CS = (ROOT / "VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallMotor/"
                   "KatamariBallMotor.cs")

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


def structure_slope(structure):
    return core.structure_slope_deg(structure)


def walkable_top(structure):
    """World Y of the highest point of a box or ramp (its walking surface at the top)."""
    return structure_aabb(structure)[1][1]


def deck_structures(doc):
    return [s for s in doc["structures"] if s["role"] in ("box", "ramp")]


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
            if cell["id"] == "C06":
                # C06 is the adversarial cell: its 50-degree board must be a BOX (the
                # authoring gate refuses role=ramp over 45 degrees and says "make it a
                # box"), so the validator's tilted-box warning is expected and is the only
                # warning allowed anywhere in the field.
                self.assertEqual(len(report.warnings), 1, "%s: %s" % (path, report.warnings))
                self.assertIn("tilted box", report.warnings[0])
                continue
            self.assertEqual(report.warnings, [], "%s: %s" % (path, report.warnings))

    def test_arena_id_matches_the_cell_and_its_folder(self):
        for cell in self.cells:
            doc = load_cell(cell["placement"])
            self.assertEqual(doc["level"]["arena"]["id"], cell["id"])
            self.assertEqual(Path(cell["placement"]).parent.name, cell["id"])

    def test_every_cell_is_a_complete_arena(self):
        # ClearObstacles zeroes the motor's global plane table, so every cell carries its own
        # floor and its own four boundary planes; a borrowed wall would leave the ball with no
        # boundary after a switch.
        for cell in self.cells:
            doc = load_cell(cell["placement"])
            roles = [structure["role"] for structure in doc["structures"]]
            self.assertEqual(roles.count("ground"), 1, cell["id"])
            self.assertEqual(roles.count("boundary"), 4, cell["id"])
            self.assertIn(roles.count("box"), (0, 1, 2, 8), cell["id"])
            self.assertIn(roles.count("ramp"), (0, 1), cell["id"])
            self.assertEqual(doc["props"], [], cell["id"])
            self.assertGreaterEqual(roles.count("box") + roles.count("ramp"), 1, cell["id"])

    def test_every_cell_has_its_own_compact_pad(self):
        # The compact field (2026-11-15): one pad per cell, the ground is exactly the cell's
        # footprint, four low boundary markers sit on its edges, and the whole field fits in
        # 28 x 31.5 m. This replaces the old "one shared 88 m base, byte-identical" rule.
        xs, zs = [], []
        for cell in self.cells:
            doc = load_cell(cell["placement"])
            ground = [s for s in doc["structures"] if s["role"] == "ground"][0]
            footprint = cell["footprint"]
            self.assertAlmostEqual(ground["center"][0], footprint["center"][0], places=4,
                                   msg=cell["id"])
            self.assertAlmostEqual(ground["center"][2], footprint["center"][1], places=4,
                                   msg=cell["id"])
            self.assertAlmostEqual(ground["size"][0], footprint["size"][0], places=4,
                                   msg=cell["id"])
            self.assertAlmostEqual(ground["size"][2], footprint["size"][1], places=4,
                                   msg=cell["id"])
            self.assertLessEqual(ground["size"][0], 6.001, cell["id"])
            self.assertLessEqual(ground["size"][2], 10.001, cell["id"])
            walls = [s for s in doc["structures"] if s["role"] == "boundary"]
            self.assertEqual(len(walls), 4, cell["id"])
            for wall in walls:
                self.assertAlmostEqual(wall["size"][2], 0.4, places=6, msg=cell["id"])
            low, high = structure_aabb(ground)
            xs.extend([low[0], high[0]])
            zs.extend([low[2], high[2]])
        self.assertLessEqual(max(xs) - min(xs), 28.001, "field is wider than the compact target")
        self.assertLessEqual(max(zs) - min(zs), 31.501, "field is deeper than the compact target")

    def test_nominal_is_the_deck_height(self):
        # The invariant is "nominal is the top the ball has to reach", so it is the highest
        # walking surface of the cell's box/ramp structures. For a step cell that is the box
        # top; for a ramp cell it is the landing the ramp meets (1.50 / 0.60); for C06 it is
        # the adversarial board's world AABB top (1.50), which is deliberately a wall.
        for cell in self.cells:
            doc = load_cell(cell["placement"])
            surfaces = deck_structures(doc)
            top = max(walkable_top(structure) for structure in surfaces)
            self.assertAlmostEqual(cell["nominal_m"], top, places=4, msg=cell["id"])

    def test_ramp_cells_declare_a_ramp_and_a_landing(self):
        ramp_cells = {"C01", "C03", "C05", "C13"}
        for cell in self.cells:
            doc = load_cell(cell["placement"])
            ramps = [s for s in doc["structures"] if s["role"] == "ramp"]
            if cell["id"] in ramp_cells:
                self.assertEqual(len(ramps), 1, cell["id"])
                self.assertIn("meets", ramps[0], cell["id"])
                landing = [s for s in doc["structures"] if s["id"] == ramps[0]["meets"]][0]
                self.assertAlmostEqual(walkable_top(landing), walkable_top(ramps[0]),
                                       places=4, msg=cell["id"])
            else:
                self.assertEqual(ramps, [], cell["id"])

    def test_ramp_slopes_are_inside_the_motor_ceiling(self):
        expected = {"C01": 5.0, "C03": 25.0, "C05": 45.0, "C13": 11.31}
        for cell in self.cells:
            if cell["id"] not in expected:
                continue
            doc = load_cell(cell["placement"])
            ramp = [s for s in doc["structures"] if s["role"] == "ramp"][0]
            slope = structure_slope(ramp)
            self.assertAlmostEqual(slope, expected[cell["id"]], delta=0.02, msg=cell["id"])
            self.assertLessEqual(slope, core.RAMP_MAX_SLOPE_DEG + 0.01, cell["id"])

    def test_c06_is_an_over_limit_box_probe_not_a_ramp(self):
        # The authoring gate refuses role=ramp over 45 degrees, so the 50-degree board must be
        # a box. Its world AABB is the design's fallback wall, and the criteria importer bakes
        # its +Y face so the driver can ask motor.AddRamp and read the refusal.
        doc = load_cell("physics_test_field/C06/placement.json")
        self.assertEqual([s for s in doc["structures"] if s["role"] == "ramp"], [])
        probes = [s for s in doc["structures"] if s.get("kind") == "ramp_probe"]
        self.assertEqual(len(probes), 1)
        probe = probes[0]
        self.assertEqual(probe["role"], "box")
        self.assertAlmostEqual(structure_slope(probe), 50.0, delta=0.02)
        low, high = structure_aabb(probe)
        self.assertAlmostEqual(high[1], 0.6, places=4)             # compact fallback top
        self.assertAlmostEqual(low[1], -0.128, delta=0.002)        # board thickness under ground
        self.assertAlmostEqual(high[2] - low[2], 0.656, delta=0.003)

    def test_new_cells_are_wired_and_not_planned(self):
        ids = {cell["id"] for cell in self.cells}
        planned = {cell["id"] for cell in self.field["planned_cells"]}
        for cell_id in ("C01", "C03", "C05", "C06", "C13"):
            self.assertIn(cell_id, ids)
            self.assertNotIn(cell_id, planned)

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

    def test_boundary_cell_expects_the_runtime_step_limit_not_the_preset_label(self):
        # C09 is the <= vs < case, but the 0.30 preset is quantized: the ball is
        # D = 0.2997369 m (one volume unit = 0.0041 m^3), so stepLimit = 0.074934 m and the
        # 0.075 m deck is ABOVE the limit. The 0.30 row therefore expects the ball to be
        # BLOCKED (on_step = fail, deck_gap = 0.075), and the driver derives that row at
        # run time from the runtime stepLimit instead of trusting the preset label.
        c09 = [cell for cell in self.cells if cell["id"] == "C09"][0]
        self.assertAlmostEqual(c09["nominal_m"], 0.075, places=6)
        self.assertLess(0.25 * 0.2997369, c09["nominal_m"],
                        "the quantized 0.30 ball cannot step 0.075 m")
        on_step = [check for check in c09["checks"] if check["id"] == "on_step"][0]
        self.assertEqual(on_step["expect"], ["fail", "pass", "pass", "pass"])
        self.assertIn("stepLimit", on_step["note"])
        # C10 is the wall case at 0.30 (0.100 > 0.074934) and climbable from 0.60.
        c10 = [cell for cell in self.cells if cell["id"] == "C10"][0]
        wall = [check for check in c10["checks"] if check["id"] == "on_step"][0]
        self.assertEqual(wall["expect"], ["fail", "pass", "pass", "pass"])

    def test_the_driver_derives_the_deck_expectation_from_the_step_limit(self):
        # The JSON row is the nominal-preset answer; the driver must re-derive it from the
        # runtime step limit, or a quantized diameter would demand an impossible climb.
        source = DRIVER_CS.read_text(encoding="utf-8")
        self.assertIn('Same(metric, "deck_gap_m")', source)
        self.assertIn("GetStepHeightLimitWorldM()", source)
        self.assertIn("GetNominalM()", source)
        # ... but never on a ramp cell: a ramp exists to lift the ball above every step
        # limit, so the derivation must be skipped exactly when the placement has a ramp.
        self.assertIn("IsRampClimb()", source)

    def test_ramp_checks_use_the_ramp_readings(self):
        metrics = {}
        for cell in self.cells:
            metrics[cell["id"]] = {check["metric"] for check in cell["checks"]}
        for cell_id in ("C01", "C03", "C05", "C13"):
            self.assertIn("ramp_support_seen", metrics[cell_id])
            self.assertIn("ramp_entry_progress_m", metrics[cell_id])
            self.assertIn("ramp_count", metrics[cell_id])
        # Only C03 keeps a mid-slope hold in the compact timeline; C05 is the 45-degree
        # equality case and its short runway has no room for a stop window.
        self.assertIn("slip_back_m", metrics["C03"])
        self.assertIn("ramp_refusal_reason_len", metrics["C06"])
        self.assertIn("ramp_refusal_slope_deg", metrics["C06"])
        self.assertIn("max_bottom_rise_m", metrics["C06"])
        self.assertIn("blocked_gap_m", metrics["C06"])
        for metric in ("landing_gap_m", "fall_frames", "support_kind", "grounded_frame_dy_m"):
            self.assertIn(metric, metrics["C13"])

    def test_the_refusal_check_expects_a_refusal_at_every_diameter(self):
        c06 = [cell for cell in self.cells if cell["id"] == "C06"][0]
        refused = [check for check in c06["checks"] if check["id"] == "ramp_refused"][0]
        self.assertEqual(refused["expect"], ["pass", "pass", "pass", "pass"])
        self.assertGreaterEqual(refused["limit"], 1)
        no_ramp = [check for check in c06["checks"] if check["id"] == "no_ramp_registered"][0]
        self.assertEqual(no_ramp["op"], "==")
        self.assertEqual(no_ramp["limit"], 0)


class SizeAccuracyAndRampLimit(unittest.TestCase):
    """The two defects the first 9-cell run at D=0.30 exposed (2026-11-15).

    Neither is a data bug, so neither is checked through the criteria file: one is the
    size setter's quantum (it used to be measured, and at 0.30 nothing is ever fed, so
    it stayed 0 and the driver reported ``unknownMetric``), the other is the 45-degree
    ramp/deck seam (the motor deadlocks there, at every diameter). These tests pin the
    code shape and the numbers the design doc promises, because both are the kind of
    thing that silently drifts back.
    """

    def test_the_quantum_is_analytic_and_has_one_implementation(self):
        setter = SETTER_CS.read_text(encoding="utf-8")
        importer = CRITERIA_CS.read_text(encoding="utf-8")
        # The one input the motor has no getter for is mirrored by the criteria importer.
        self.assertIn("sizeModelBaselineVolumeUnits", setter)
        self.assertIn("sizeModelBaselineVolumeUnits", importer)
        self.assertIn('FindProperty("baselineVolumeUnits")', importer)
        self.assertIn("GetConfirmedVolumeUnits()", setter)
        # ... and the old measured path must be gone, not kept as a second source.
        for removed in ("lastCubedDiameter", "lastVolumeUnits", "hasSample"):
            self.assertNotIn(removed, setter)

    def test_the_size_accuracy_expectation_matches_the_analytic_quantum(self):
        # quantum = D / (3 * (baseline + confirmed)); at the 0.30 tier the ball is at the
        # baseline after the reset, so the driver's value is this error in quanta. The
        # design doc's expected console line has to be that number, not 0.
        motor = MOTOR_CS.read_text(encoding="utf-8")
        baseline = float(re.search(r"baselineVolumeUnits = ([0-9.]+)f", motor).group(1))
        cubic = float(re.search(r"cubicMetersPerVolumeUnit = ([0-9.]+)f", motor).group(1))
        diameter = (6.0 * baseline * cubic / math.pi) ** (1.0 / 3.0)
        quantum = diameter / (3.0 * baseline)
        value = abs(diameter - 0.30) / quantum
        # The driver prints with Fixed(): value * 10000 + 0.5 truncated.
        printed = int(value * 10000.0 + 0.5) / 10000.0
        self.assertAlmostEqual(printed, 0.0091, places=6)
        doc = (ROOT / "Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md").read_text(encoding="utf-8")
        self.assertIn("value=+0.0091 op=<=1.0000", doc)
        # The old, wrong claim: one unit is 58 mm at D=0.30. It is 29.0 mm. (The doc may
        # mention the old number as a correction, but must never present it as the value.)
        self.assertNotIn("D=0.30 时 58 mm", doc)
        self.assertIn("29.0 mm", doc)

    def test_the_driver_separates_a_wrong_metric_name_from_an_unavailable_one(self):
        driver = DRIVER_CS.read_text(encoding="utf-8")
        self.assertIn("private int TryReadMetric(string metric, out float value)", driver)
        self.assertIn("return MetricUnknown;", driver)
        self.assertIn("return MetricSizeModelMissing;", driver)
        self.assertIn("return MetricNoSetter;", driver)
        self.assertIn('" reason=" + MetricReason(metricStatus)', driver)
        # The ALERT must not print the old catch-all that made a missing sample look like a typo.
        self.assertNotIn('error=unknownMetric', driver)

    def test_the_c05_limit_is_documented_as_a_physics_finding(self):
        # C05 (45 deg) fails `climbed` at every diameter. That is the motor's seam
        # deadlock, not a timeline: the design doc carries the derivation and the angle
        # limit (asin(0.6) = 36.87 deg), and the data stays untouched.
        doc = (ROOT / "Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md").read_text(encoding="utf-8")
        self.assertIn("### 4.1.3", doc)
        self.assertIn("asin(0.6) = 36.87", doc)
        self.assertIn("0.0878", doc)
        field = load_field()
        c05 = [cell for cell in field["cells"] if cell["id"] == "C05"][0]
        climbed = [check for check in c05["checks"] if check["id"] == "climbed"][0]
        # The criterion is NOT relaxed to paper over the limit.
        self.assertEqual(climbed["limit"], 0.01)
        self.assertIn("pass", climbed["expect"])
        self.assertEqual(c05["drive"][-1]["t"], 2.5)


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
