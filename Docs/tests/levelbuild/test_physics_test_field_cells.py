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
  adversarial cell must declare a ``kind=ramp_probe`` box instead, because C06 is the
  *motor's* over-45 refusal probe (``AddRamp`` still refuses there until v2 lands) - the
  authoring gate would now only warn about a 50-degree ramp (``CONTACT_MODEL_V2`` 1.5/S4),
  so the probe is a deliberate data choice, not a gate workaround;
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
# The v1 rows frozen once, with every check's optional `v2` block stripped. A snapshot - not
# `git show HEAD:` - because the v2 rows are themselves committed now, and because a clean
# checkout with no history must still be able to prove "the v1 matrix did not move".
V1_SNAPSHOT = ROOT / "Docs/tests/levelbuild/physics_test_field_v1_snapshot.json"
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
        # 9 wired + 12 planned C-cells = the 21-cell field. The v2-only design rows (W1-W6,
        # R1/R2, S1, X1) describe scenes that do not exist yet, so they are not part of that
        # count - they are inert data until someone authors their placement.
        design_only = [cell for cell in self.doc["planned_cells"]
                       if cell.get("status") == "pending_scene"]
        self.assertEqual(len(design_only), 10)
        self.assertEqual(len(ids) + len(planned) - len(design_only), 21)

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
                # C06 is the adversarial cell: its 50-degree board is authored as a BOX on
                # purpose (the motor's AddRamp refusal probe), so the validator's tilted-box
                # warning is expected and is the only warning allowed anywhere in the field.
                # A role=ramp at 50 degrees would only warn since CONTACT_MODEL_V2 1.5/S4.
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
        # C06 is the motor's over-45 refusal probe, so the 50-degree board is a box by
        # design (the authoring gate would only warn about a 50-degree ramp since
        # CONTACT_MODEL_V2 1.5/S4). Its world AABB is the design's fallback wall, and the
        # criteria importer bakes its +Y face so the driver can ask motor.AddRamp and read
        # the refusal.
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


V2_KEYS = {"expect", "metric", "op", "limit", "threshold", "not_applicable", "note"}
V2_THRESHOLDS = ("climb_cap_bound",)
V2_EXPECT_VALUES = ("pass", "fail", "n/a")
DESIGN_ONLY_IDS = ("W1", "W2", "W3", "W4", "W5", "W6", "R1", "R2", "S1", "X1")


def v1_view(doc):
    """The criteria file with every check's optional `v2` block removed (cell order kept)."""
    rows = []
    for cell in doc["cells"]:
        rows.append({
            "cell": {key: value for key, value in cell.items() if key != "checks"},
            "checks": [{key: value for key, value in check.items() if key != "v2"}
                       for check in cell["checks"]],
        })
    return rows


def write_v1_snapshot():
    """Writes the frozen v1 rows. Run ONLY when the v1 criteria change on purpose."""
    doc = load_field()
    rows = v1_view(doc)
    snapshot = {
        "_meta": {
            "what": "the v1 rows of Docs/Design/BallMotor/physics_test_field.json, with every "
                    "check's optional `v2` block removed",
            "why": "with contactModelV2Enabled=false the 36-run matrix must stay byte-identical to "
                   "pass7. TestFieldCriteriaModels compares this file against the working tree; a "
                   "snapshot instead of `git show HEAD:` keeps that proof in a clean checkout and "
                   "after the v2 rows were themselves committed (a4c9f4a)",
            "source_commit": "9a71727 (the commit before the v2 overrides; verified equal to the "
                             "working tree's v1 view when the snapshot was written)",
            "generated_by": "python3 Docs/tests/levelbuild/test_physics_test_field_cells.py "
                            "--write-v1-snapshot",
            "regenerate_when": "only when a v1 criterion is changed on purpose - which also "
                               "invalidates criteria_models.v1_baseline_pass7",
            "cells": len(rows),
            "checks": sum(len(row["checks"]) for row in rows),
        },
        "cells": rows,
    }
    V1_SNAPSHOT.write_text(json.dumps(snapshot, indent=2, ensure_ascii=False) + "\n",
                           encoding="utf-8")
    print("wrote " + str(V1_SNAPSHOT) + ": cells=" + str(snapshot["_meta"]["cells"])
          + " checks=" + str(snapshot["_meta"]["checks"]))


def v2_rows(doc):
    """(cell, check, v2 block) for every wired criterion that carries an override."""
    for cell in doc["cells"]:
        if not cell.get("wired"):
            continue
        for check in cell["checks"]:
            if "v2" in check:
                yield cell, check, check["v2"]


def v2_row(doc, cell_id, check_id):
    for cell, check, v2 in v2_rows(doc):
        if cell["id"] == cell_id and check["id"] == check_id:
            return v2
    raise AssertionError("no v2 override on %s/%s" % (cell_id, check_id))


class CriteriaModels(unittest.TestCase):
    """The criteria file's model dimension (design PHYSICS_TEST_FIELD section 5.6).

    The same 36-run matrix has to be meaningful for both motor models: v1 (the flag off) is the
    pass7 baseline, v2 (the flag on) is CONTACT_MODEL_V2 6.1/7.7.2/7.7.4. One criteria file, one
    optional ``v2`` block per criterion - so these tests pin three things: the blocks are a closed
    vocabulary, every name they introduce is readable, and the v1 rows are byte-identical to git
    HEAD (the matrix must not move when the flag is off).
    """

    def setUp(self):
        self.doc = load_field()
        self.cells = {cell["id"]: cell for cell in self.doc["cells"] if cell.get("wired")}
        self.metrics = driver_metrics()

    def test_v2_blocks_use_a_closed_vocabulary(self):
        seen = 0
        for cell, check, v2 in v2_rows(self.doc):
            seen += 1
            where = "%s/%s" % (cell["id"], check["id"])
            self.assertTrue(set(v2) <= V2_KEYS, "%s: %s" % (where, set(v2) - V2_KEYS))
            if "op" in v2:
                self.assertIn(v2["op"], OPS, where)
                # An op override carries its own limit, or names a threshold instead. Anything
                # else would silently compare against 0.
                self.assertNotEqual(("limit" in v2), ("threshold" in v2), where)
            if "threshold" in v2:
                self.assertIn(v2["threshold"], V2_THRESHOLDS, where)
            if "expect" in v2:
                self.assertEqual(len(v2["expect"]), 4, where)
                for value in v2["expect"]:
                    self.assertIn(value, V2_EXPECT_VALUES, where)
            if v2.get("not_applicable"):
                # "n/a at every diameter" and a pass/fail row are contradictory answers.
                self.assertNotIn("pass", v2.get("expect", []), where)
                self.assertNotIn("fail", v2.get("expect", []), where)
            if "note" in v2:
                self.assertTrue(v2["note"].strip(), where)
        self.assertEqual(seen, 11, "the v2 table shrank or grew; update this test with it")

    def test_v2_overrides_only_exist_on_wired_criteria(self):
        # An override on a cell the driver never runs would be dead data, and a v2 row that names a
        # criterion the v1 rows do not carry has nothing to override.
        for cell in self.doc["cells"]:
            self.assertTrue(cell.get("wired"), "cells[] only holds wired cells: " + cell["id"])
            self.assertTrue(cell["checks"], cell["id"])
            for check in cell["checks"]:
                if "v2" not in check:
                    continue
                self.assertIn("expect", check, "%s/%s: the v1 row is the thing being overridden"
                              % (cell["id"], check["id"]))
                self.assertTrue(check["note"].strip(), "%s/%s" % (cell["id"], check["id"]))
        # The pending design rows are v2-only and carry design_checks, never a v1 `checks` array.
        for cell in self.doc["planned_cells"]:
            if cell.get("status") != "pending_scene":
                continue
            self.assertNotIn("checks", cell, cell["id"])
            for check in cell["design_checks"]:
                self.assertNotIn("v2", check, "%s/%s" % (cell["id"], check["id"]))

    def test_every_v2_metric_is_readable_by_the_driver(self):
        self.assertTrue(self.metrics, "no metrics parsed from the driver")
        for cell, check, v2 in v2_rows(self.doc):
            if "metric" not in v2:
                continue
            self.assertIn(v2["metric"], self.metrics,
                          "%s/%s names a v2 metric the driver cannot read" %
                          (cell["id"], check["id"]))

    def test_v1_rows_match_the_frozen_snapshot(self):
        # The whole point of putting v2 in its own block: with the flag off the matrix must be
        # exactly pass7. The snapshot was generated once from 9a71727 (the commit before the v2
        # rows), so this proves it without git history and keeps working now that the v2 rows are
        # committed too.
        self.assertTrue(V1_SNAPSHOT.is_file(), V1_SNAPSHOT)
        snapshot = json.loads(V1_SNAPSHOT.read_text(encoding="utf-8"))
        self.assertIn("9a71727", snapshot["_meta"]["source_commit"])
        self.assertEqual(v1_view(self.doc), snapshot["cells"],
                         "a v1 row changed; if that is intentional, regenerate the snapshot "
                         "with --write-v1-snapshot and update the pass7 counts")
        self.assertEqual(snapshot["_meta"]["checks"], 59)
        self.assertEqual(snapshot["_meta"]["cells"], 9)

    def test_v2_overrides_are_additive_only(self):
        # Every override hangs off a criterion the snapshot already had, and the snapshot itself
        # carries no v2 block at all - so "v2 was added" and "v1 was edited" cannot be confused.
        snapshot = json.loads(V1_SNAPSHOT.read_text(encoding="utf-8"))
        known = {}
        for row in snapshot["cells"]:
            known[row["cell"]["id"]] = [check["id"] for check in row["checks"]]
            for check in row["checks"]:
                self.assertNotIn("v2", check, "%s/%s" % (row["cell"]["id"], check["id"]))
        for cell, check, _ in v2_rows(self.doc):
            self.assertIn(cell["id"], known)
            self.assertIn(check["id"], known[cell["id"]],
                          "%s/%s carries a v2 block but no v1 criterion" %
                          (cell["id"], check["id"]))
        # and the v1 row list itself is unchanged, in order
        for row in snapshot["cells"]:
            working = self.cells[row["cell"]["id"]]
            self.assertEqual([check["id"] for check in working["checks"]],
                             known[row["cell"]["id"]], row["cell"]["id"])

    def test_the_v2_table_matches_the_design(self):
        # CONTACT_MODEL_V2 7.7.2 "保持 / 翻转 / 替换" plus 7.7.4 for C14, one row each.
        for cell_id in ("C01", "C03", "C05", "C13"):
            v2 = v2_row(self.doc, cell_id, "ramp_registered")
            self.assertEqual(v2["metric"], "static_obb_count", cell_id)
            self.assertEqual((v2["op"], v2["limit"]), (">=", 1), cell_id)
            self.assertEqual(v2["expect"], ["pass"] * 4, cell_id)
        self.assertTrue(v2_row(self.doc, "C06", "no_ramp_registered")["not_applicable"])
        no_climb = v2_row(self.doc, "C06", "no_climb")
        self.assertEqual(no_climb["op"], "<=")
        self.assertEqual(no_climb["threshold"], "climb_cap_bound")
        self.assertEqual(no_climb["expect"], ["pass"] * 4)
        stopped = v2_row(self.doc, "C06", "stopped_by_wall")
        self.assertEqual((stopped["op"], stopped["limit"]), ("<=", 0.0))
        self.assertEqual(stopped["expect"], ["pass"] * 4)
        for cell_id in ("C09", "C10"):
            on_step = v2_row(self.doc, cell_id, "on_step")
            self.assertEqual(on_step["expect"], ["pass"] * 4, cell_id)
            self.assertNotIn("metric", on_step)
        blocked = v2_row(self.doc, "C10", "blocked_gap")
        self.assertEqual((blocked["op"], blocked["limit"]), ("<=", 0.0))
        self.assertEqual(blocked["expect"], ["pass"] * 4)
        lift = v2_row(self.doc, "C14", "lift_bounded")
        self.assertEqual(lift["metric"], "lift_over_nominal_in_deck_m")
        self.assertEqual((lift["op"], lift["limit"]), ("<=", 0.01))
        self.assertEqual(lift["expect"], ["pass"] * 4)
        self.assertAlmostEqual(self.cells["C14"]["nominal_m"], 0.028, places=6)

    def test_the_ramp_refusal_rows_deliberately_keep_the_v1_expectation(self):
        # 7.7.2 predicted "v2 refuses no ramp", but the probe goes through motor.AddRamp, whose
        # 45-degree support guard is v1-side and independent of the flag: pass6 and pass8 both
        # recorded reason=..., slope=50.00, ramp_refused=36 chars. The evidence wins over the
        # prediction, so these two rows keep one criterion for both models.
        for check_id in ("ramp_refused", "refusal_slope"):
            for check in self.cells["C06"]["checks"]:
                if check["id"] == check_id:
                    self.assertNotIn("v2", check, check_id)

    def test_rest_and_seam_keep_one_definition(self):
        # "保持" rows: rest_gap already passes under both models (pass8 cleared it), and the v1
        # definitions of the seam/landing rows stay the criterion.
        for cell_id, check_id in (("C09", "rest_gap"), ("C08", "rest_gap"),
                                  ("C10", "rest_gap"), ("C13", "seam_no_step"),
                                  ("C13", "climbed"), ("C05", "climbed")):
            for check in self.cells[cell_id]["checks"]:
                if check["id"] == check_id:
                    self.assertNotIn("v2", check, "%s/%s" % (cell_id, check_id))

    def test_the_expected_counts_are_recorded_for_both_models(self):
        # The numbers an operator compares a matrix against. v1 is pass7 verbatim; v2 is computed
        # from the pass8 raw values (59 checks per run in both models).
        models = self.doc["criteria_models"]
        self.assertEqual(models["flag"], "KatamariBallMotor.IsContactModelV2()")
        v1 = models["v1_baseline_pass7"]["per_run"]
        self.assertEqual(v1, {
            "0.300": "58 pass / 0 skip / 1 unexpected",
            "0.600": "57 / 1 / 1",
            "1.100": "56 / 2 / 1",
            "2.200": "52 / 5 / 2",
        })
        v2 = models["v2_expected"]["per_run"]
        self.assertEqual(v2, {
            "0.300": "58 pass / 1 skip / 0 unexpected",
            "0.600": "58 / 1 / 0",
            "1.100": "57 / 2 / 0",
            "2.200": "54 / 5 / 0",
        })
        # 59 checks per run: 9 wired cells' rows at one diameter.
        per_run = sum(len(cell["checks"]) for cell in self.doc["cells"] if cell.get("wired"))
        self.assertEqual(per_run, 59)

    def test_the_pending_scene_cells_are_design_rows_only(self):
        design = [cell for cell in self.doc["planned_cells"]
                  if cell.get("status") == "pending_scene"]
        self.assertEqual(tuple(cell["id"] for cell in design), DESIGN_ONLY_IDS)
        for cell in design:
            self.assertFalse(cell.get("wired"), cell["id"])
            self.assertIsNone(cell.get("placement"), cell["id"])
            self.assertTrue(cell.get("v2_only"), cell["id"])
            self.assertTrue(cell.get("design_source", "").startswith("CONTACT_MODEL_V2.md"),
                            cell["id"])
            self.assertTrue(cell["design_checks"], cell["id"])
            for check in cell["design_checks"]:
                where = "%s/%s" % (cell["id"], check["id"])
                self.assertIn(check["op"], OPS, where)
                self.assertEqual(len(check["expect"]), 4, where)
                for value in check["expect"]:
                    self.assertIn(value, V2_EXPECT_VALUES, where)
                self.assertTrue(check["unit"], where)
            # Every new cell says which reading it still needs; an empty list would mean the
            # design row is already measurable, i.e. the cell could be wired today.
            self.assertIsInstance(cell.get("metrics_pending"), list, cell["id"])

    def test_the_driver_selects_the_row_from_the_motor_flag(self):
        driver = DRIVER_CS.read_text(encoding="utf-8")
        self.assertIn('DriverRevision = "2026-11-15e"', driver)
        self.assertIn("modelIsV2 = motor.IsContactModelV2()", driver)
        self.assertIn("cell.HasCheckV2(i)", driver)
        self.assertIn("GetCheckV2Expect", driver)
        self.assertIn("GetCheckV2Threshold", driver)
        self.assertIn('" model=" + ModelText()', driver)
        # the v2 threshold is derived from the motor's own constants, not from a metre value
        self.assertIn("ContactTuning.KHeight", driver)
        self.assertIn("ContactTuning.ClimbCapMarginM", driver)
        self.assertIn("V2CapFloatMargin", driver)
        # ... and the plate gate is data + radius, not a constant
        self.assertIn("cell.IsInDeckGate(center.x, center.z, radius)", driver)
        self.assertIn('Same(metric, "lift_over_nominal_in_deck_m")', driver)
        self.assertIn('Same(metric, "static_obb_count")', driver)
        # the v1 derivations must not run for a v2 row
        self.assertIn("if (!v2Row && Same(metric, \"deck_gap_m\")", driver)
        self.assertIn("if (!v2Row && Same(metric, \"ramp_entry_progress_m\")", driver)

    def test_the_cell_and_importer_carry_the_v2_row(self):
        cell = CELL_CS.read_text(encoding="utf-8")
        for field in ("checkHasV2", "checkV2Metrics", "checkV2Ops", "checkV2Limits",
                      "checkV2ExpectMasks", "checkV2Thresholds", "deckCenterXZ",
                      "deckHalfSizeM"):
            self.assertIn(field, cell)
        self.assertIn('CellRevision = "2026-11-15e"', cell)
        importer = CRITERIA_CS.read_text(encoding="utf-8")
        for symbol in ("V2ExpectMask", "V2OpCode", "V2ThresholdCode", "climb_cap_bound",
                       "checkV2ExpectMasks", "checkV2Thresholds", "V2Rows"):
            self.assertIn(symbol, importer)
        # the importer must bake the deck footprint from the arena, not from the criteria file
        self.assertIn('FindProperty("staticBoxes")', importer)
        self.assertIn("deckCenterXZ", importer)


def method_body(source, signature):
    """The body of one C# method, by brace matching.

    The per-frame guard needs the body boundary, not a window of lines: a comment or a later
    method must not be able to hide a call that only looks like it is inside TickSize.
    """
    start = source.index(signature)
    brace = source.index("{", start + len(signature))
    depth = 0
    for i in range(brace, len(source)):
        char = source[i]
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return source[brace + 1:i]
    raise AssertionError("unbalanced method: " + signature)


class _SetterModel:
    """Minimal model of LabBallSizeSetter: request state plus one Feed per update tick.

    Same rules as the C#: D^3 is linear in (baseline + confirmed), the quantum is D/(3*total),
    convergence allows max(tolerance, quantum), whole units are rounded in the cube domain, and
    one candidate is in flight at a time. A commit lands at the start of the next tick.
    """

    def __init__(self, baseline, cubic, tolerance=0.002):
        self.baseline = baseline
        self.cubic = cubic
        self.tolerance = tolerance
        self.confirmed = 0.0
        self.pending = 0.0
        self.target = 0.0
        self.request_active = False
        self.converged = False
        self.absorbs = 0
        self.failure = ""

    def diameter(self):
        return (6.0 * self.cubic * (self.baseline + self.confirmed) / math.pi) ** (1.0 / 3.0)

    def set_diameter(self, target):
        # the C# body: clear every per-request field, then feed from Update
        self.target = target
        self.request_active = True
        self.converged = False
        self.absorbs = 0
        self.failure = ""

    def tick(self):
        if self.pending:
            self.confirmed += self.pending
            self.pending = 0.0
        if not self.request_active or self.converged:
            return
        diameter = self.diameter()
        cubed = diameter * diameter * diameter
        total = self.baseline + self.confirmed
        if total <= 0:
            return
        slope = cubed / total
        quantum = diameter / (3.0 * total)
        error = abs(diameter - self.target)
        allowed = max(self.tolerance, quantum)
        if error <= allowed:
            self.converged = True
            return
        if diameter > self.target + allowed:
            self.failure = "cannotShrink"
            self.request_active = False
            return
        units = int((self.target ** 3 - cubed) / slope + 0.5)
        if units < 1:
            self.converged = True
            return
        after = cubed + units * slope
        if abs(self.target ** 3 - after) >= abs(self.target ** 3 - cubed):
            self.converged = True
            return
        self.pending = float(units)
        self.absorbs += 1

    def settle(self):
        """One run's size stage is over: the round reset re-prices the ball to the baseline."""
        self.confirmed = 0.0
        self.pending = 0.0


def _run_old_design(targets, baseline, cubic, order, max_ticks=200):
    """The pass-2 shape: TickSize calls SetDiameterM every frame; a call after convergence
    starts a new request, so converged is cleared before it can be observed."""
    setter = _SetterModel(baseline, cubic)
    absorbs = 0
    for target in targets:
        setter.settle()
        observed = False
        for _ in range(max_ticks):
            if order == "driver_first":
                setter.set_diameter(target)
                if setter.converged:
                    observed = True
                    break
                setter.tick()
            else:
                setter.tick()
                setter.set_diameter(target)
                if setter.converged:
                    observed = True
                    break
        absorbs += setter.absorbs
        if not observed:
            return "timeout", absorbs
    return "ok", absorbs


def _run_new_design(targets, baseline, cubic, order, max_ticks=200):
    """The final shape: Switch issues the one request; the size stage only observes."""
    setter = _SetterModel(baseline, cubic)
    absorbs = 0
    for target in targets:
        setter.settle()
        setter.set_diameter(target)          # Switch, once
        observed = False
        for _ in range(max_ticks):
            if order == "driver_first":
                if setter.converged or setter.failure:
                    observed = True
                    break
                setter.tick()
            else:
                setter.tick()
                if setter.converged or setter.failure:
                    observed = True
                    break
        absorbs += setter.absorbs
        if not observed:
            return "timeout", absorbs
        if setter.failure:
            return "blocked:" + setter.failure, absorbs
        if abs(setter.diameter() - target) > 0.02:
            return "sizeNotApplied", absorbs
    return "ok", absorbs


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
        self.assertIn("sizeModelStartVolumeMm3", setter)
        self.assertIn("sizeModelStartVolumeMm3", importer)
        self.assertIn('FindProperty("startVolumeMm3")', importer)
        self.assertIn("GetConfirmedVolumeMm3()", setter)
        # ... and the old measured path must be gone, not kept as a second source.
        for removed in ("lastCubedDiameter", "lastVolumeUnits", "hasSample"):
            self.assertNotIn(removed, setter)

    def test_the_size_accuracy_expectation_matches_the_analytic_quantum(self):
        # quantum = D / (3 * (baseline + confirmed)); at the 0.30 tier the ball is at the
        # baseline after the reset, so the driver's value is this error in quanta. The
        # design doc's expected console line has to be that number, not 0.
        motor = MOTOR_CS.read_text(encoding="utf-8")
        # Steps C+D: the start volume is mm^3 and one quantum is 1 mm^3, so the analytic
        # quantum is D / (3 * totalMm3) metres. The old unit-based numbers (29.0 mm / +0.0091)
        # belonged to 0.0041 m^3 units and are retired with them; the field's expected console
        # line is re-pinned by the operator's next run (PHYSICS_TEST_FIELD.md), so this test
        # pins the QUANTITY and its derivation instead of a stale doc string.
        start_mm3 = float(re.search(r"startVolumeMm3 = ([0-9]+)L", motor).group(1))
        diameter = (6.0 * start_mm3 * 1e-9 / math.pi) ** (1.0 / 3.0)
        quantum = diameter / (3.0 * start_mm3)
        self.assertAlmostEqual(diameter, 0.2997, places=3)
        self.assertAlmostEqual(quantum * 1e6, 0.0070876, places=6,
                               msg="one mm^3 is ~7.1 nm of diameter at the start ball")
        value = abs(diameter - 0.30) / quantum
        printed = int(value * 10000.0 + 0.5) / 10000.0
        self.assertGreater(printed, 40000.0,
                           "a 0.3 mm start offset is tens of thousands of mm^3 quanta")

    def test_the_driver_separates_a_wrong_metric_name_from_an_unavailable_one(self):
        driver = DRIVER_CS.read_text(encoding="utf-8")
        self.assertIn("private int TryReadMetric(string metric, out float value)", driver)
        self.assertIn("return MetricUnknown;", driver)
        self.assertIn("return MetricSizeModelMissing;", driver)
        self.assertIn("return MetricNoSetter;", driver)
        self.assertIn('" reason=" + MetricReason(metricStatus)', driver)
        # The ALERT must not print the old catch-all that made a missing sample look like a typo.
        self.assertNotIn('error=unknownMetric', driver)

    def test_the_size_request_is_issued_once_and_the_poll_is_read_only(self):
        # pass-1: a "same target" short-circuit kept the previous run's converged flag, so only
        # the first cell per tier was resized. pass-2: the fix re-issued SetDiameterM from the
        # poll loop every frame, which cleared converged every frame, so TickSize never observed
        # it and the size stage timed out on a ball already at the target. The final rule is:
        # one request, issued in Switch after the reset; the size stage only reads.
        driver = DRIVER_CS.read_text(encoding="utf-8")
        setter = SETTER_CS.read_text(encoding="utf-8")
        switch = method_body(driver, "private void TickSwitch()")
        size = method_body(driver, "private void TickSize()")

        self.assertEqual(driver.count("ballSize.SetDiameterM("), 1)
        self.assertIn("ballSize.SetDiameterM(", switch)
        # the request must come AFTER the reset that re-prices the ball back to the baseline
        self.assertLess(switch.index("TryResetForNewRound"),
                        switch.index("ballSize.SetDiameterM("))
        # the size stage and its poll never write the request state
        for forbidden in ("SetDiameterM(", "BeginRequest"):
            self.assertNotIn(forbidden, size, "TickSize must only observe the setter")
        self.assertNotIn("BeginRequest", driver)
        self.assertNotIn("BeginRequest", setter)
        # convergence must still mean the ball IS the target
        self.assertIn("SizeAppliedToleranceM", size)
        self.assertIn("reason=sizeNotApplied", size)

    def test_the_setter_request_clears_every_per_request_field(self):
        setter = SETTER_CS.read_text(encoding="utf-8")
        body = method_body(setter, "public void SetDiameterM(float targetMeters)")
        # no same-target short-circuit: every call is a new request (pass-1 defect)
        self.assertNotIn("Mathf.Abs(targetMeters - targetM)", body)
        for field in ("requestActive = true", "converged = false", "absorbsThisRequest = 0",
                      "absorbCount = 0", "nextFeedTime = 0f", "lastFailure = ", "lastErrorM = 0f"):
            self.assertIn(field, body, field)
        # converged is cleared in exactly one place: the request entry point
        self.assertEqual(setter.count("converged = false"), 1)

    def test_the_state_machine_simulation_converges_on_every_path(self):
        # A minimal model of the two state machines (same formulas as the C#): the pass-2 shape
        # must deadlock, the final shape must converge, for both Update orders (the order between
        # two U# behaviours is undefined). This is the guard that reproduces the timeout instead
        # of only pinning the source text.
        motor = MOTOR_CS.read_text(encoding="utf-8")
        # The simulation model is generic in (baseline, cubic): mm^3 with 1e-9 m^3 per unit
        # is the same algebra the setter uses now, so only the constants change.
        baseline = float(re.search(r"startVolumeMm3 = ([0-9]+)L", motor).group(1))
        cubic = 1e-9
        for order in ("driver_first", "setter_first"):
            # pass-2 shape: TickSize re-issues the request every frame
            self.assertEqual(_run_old_design([0.30], baseline, cubic, order),
                             ("timeout", 0), order)
            # final shape: 0.30 already at the target -> no feed; the other tiers -> one feed
            self.assertEqual(_run_new_design([0.30], baseline, cubic, order), ("ok", 0), order)
            for target in (0.60, 1.10, 2.20):
                self.assertEqual(_run_new_design([target], baseline, cubic, order),
                                 ("ok", 1), (target, order))
            # same tier, nine consecutive cells: every run must re-feed
            self.assertEqual(_run_new_design([0.60] * 9, baseline, cubic, order),
                             ("ok", 9), order)
            # tier changes: 0.30 -> 0.60 -> 1.10 -> 2.20
            self.assertEqual(_run_new_design([0.30, 0.60, 1.10, 2.20], baseline, cubic, order),
                             ("ok", 3), order)

    def test_the_quantum_is_live_not_a_cached_snapshot(self):
        # The old getter returned lastQuantumM, set only inside Feed; after a short-circuited
        # request that was the PREVIOUS run's quantum (the +41.27 / +371.65 / +3523.00
        # constants in the matrix). It is now computed from the live ball on every read.
        setter = SETTER_CS.read_text(encoding="utf-8")
        # No cached quantum field and no getter that returns one (the doc may name the old
        # field while explaining why it is gone).
        self.assertNotIn("private float lastQuantumM", setter)
        self.assertNotIn("return lastQuantumM", setter)
        self.assertIn("private void ReadSizeModel(out float slopeCubedPerMm3, out float quantumM)",
                      setter)
        self.assertIn("ReadSizeModel(out slope, out quantum)", setter)

    def test_the_running_program_is_stamped_and_p3_is_not_a_skip(self):
        # The matrix ran a pre-5835517 driver against post-5835517 data: it printed
        # BLOCKED_UNTIL_P3 and had no ramp-cell exemption. Both are gone from the source,
        # and each component stamps its revision so a stale U# program is visible in the
        # first line of the log.
        driver = DRIVER_CS.read_text(encoding="utf-8")
        setter = SETTER_CS.read_text(encoding="utf-8")
        self.assertNotIn("BLOCKED_UNTIL_P3", driver)
        self.assertIn("[ptf] build=", driver)
        self.assertIn("DriverRevision", driver)
        self.assertIn("[ptf] setter build=", setter)
        self.assertIn("SetterRevision", setter)
        # A ramp cell that lost its rampClimb flag is a stale import, not physics.
        self.assertIn("rampCellMismatch", driver)

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



def rotate_quat(quat, vector):
    """Rotate a vector by a quaternion (xyzw), for reading ramp axes out of a placement."""
    x, y, z, w = quat
    vx, vy, vz = vector
    # t = 2 * cross(q.xyz, v)
    tx = 2.0 * (y * vz - z * vy)
    ty = 2.0 * (z * vx - x * vz)
    tz = 2.0 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx))


def ramp_geometry(placement):
    """(horizontal run, slope in degrees) of the placement's ramp, from its box + quaternion."""
    ramp = [s for s in placement["structures"] if s.get("role") == "ramp"][0]
    axis_y = rotate_quat(ramp["rotationQuat"], (0.0, 1.0, 0.0))
    slope = math.degrees(math.acos(max(-1.0, min(1.0, axis_y[1]))))
    run = ramp["size"][2] * math.cos(math.radians(slope))
    return run, slope


def ramp_applies(check_id, diameter, nominal, run, slope):
    """Design 4.1.4: R1 ramp_support, R2 ramp_entered, R3 landing checks."""
    r = diameter / 2.0
    theta = math.radians(slope)
    if check_id == "ramp_support":
        return run - r > 0.0
    if check_id == "ramp_entered":
        # Deadlock when the deck is still not steppable where the expanded deck face stops the
        # ball; then the reachable travel past the foot is run - r, else the whole run.
        deadlock = nominal - (run - r) * math.tan(theta) - r * (1.0 / math.cos(theta) - 1.0) > 0.5 * r
        reachable = (run - r) if deadlock else run
        return reachable >= 0.25 * diameter
    if check_id in ("landing_gap", "landed_on_ground"):
        return nominal >= r
    return True


class RampApplicability(unittest.TestCase):
    """The A-class tiers: rules R1/R2/R3 recomputed from geometry, not trusted from the file.

    Design 4.1.4 says a ramp-climb criterion is not applicable when the ball cannot use the
    ramp. The rule lives in one place (this test) and the JSON row must agree, so a future
    edit cannot quietly turn a red criterion into "not applicable".
    """

    def setUp(self):
        self.field = load_field()
        self.diameters = [d["diameter_m"] for d in self.field["diameters"]]
        self.cells = {c["id"]: c for c in self.field["cells"] if c.get("wired")}

    def test_applies_d_rows_are_four_booleans(self):
        for cell in self.cells.values():
            for check in cell["checks"]:
                if "applies_d" not in check:
                    continue
                self.assertEqual(len(check["applies_d"]), 4, (cell["id"], check["id"]))
                self.assertTrue(all(isinstance(v, bool) for v in check["applies_d"]),
                                (cell["id"], check["id"]))
                self.assertTrue(any(check["applies_d"]),
                                "%s/%s excludes every diameter; remove the check instead"
                                % (cell["id"], check["id"]))

    def test_ramp_rows_match_the_geometry(self):
        applicable_checks = ("ramp_support", "ramp_entered")
        seen = 0
        for cell in self.cells.values():
            placement = load_cell(cell["placement"])
            if placement is None:
                continue
            if not any(s.get("role") == "ramp" for s in placement["structures"]):
                continue
            run, slope = ramp_geometry(placement)
            for check in cell["checks"]:
                if check["id"] not in applicable_checks:
                    continue
                seen += 1
                expected = [ramp_applies(check["id"], d, cell["nominal_m"], run, slope)
                            for d in self.diameters]
                actual = check.get("applies_d", [True, True, True, True])
                self.assertEqual(actual, expected,
                                 "%s/%s: geometry says %s, data says %s (run=%.3f slope=%.2f)"
                                 % (cell["id"], check["id"], expected, actual, run, slope))
        self.assertGreaterEqual(seen, 8, "ramp cells' support/entry rows must both be checked")

    def test_landing_rows_match_the_deck_height(self):
        seen = 0
        for cell in self.cells.values():
            for check in cell["checks"]:
                if check["id"] not in ("landing_gap", "landed_on_ground"):
                    continue
                seen += 1
                expected = [cell["nominal_m"] >= d / 2.0 for d in self.diameters]
                actual = check.get("applies_d", [True, True, True, True])
                self.assertEqual(actual, expected, (cell["id"], check["id"]))
        self.assertGreaterEqual(seen, 2)

    def test_the_real_reds_stay_applicable(self):
        # C05's climbed is the 45-degree limit probe and C13's seam_no_step is the defect-note
        # evidence: neither may be retired by an applies_d row.
        c05 = self.cells["C05"]
        climbed = [c for c in c05["checks"] if c["id"] == "climbed"][0]
        self.assertEqual(climbed.get("applies_d", [True] * 4), [True] * 4)
        self.assertEqual(climbed["limit"], 0.01)
        c13 = self.cells["C13"]
        seam = [c for c in c13["checks"] if c["id"] == "seam_no_step"][0]
        self.assertEqual(seam.get("applies_d", [True] * 4), [True] * 4)
        self.assertEqual(seam["limit"], 0.05)

    def test_the_ramp_entry_threshold_is_derived_not_absolute(self):
        # B-class fix (design 4.1.4): the driver derives one step-limit of travel instead of the
        # old absolute 0.30, which sat 0.4 mm above the 45-degree ramp's geometric maximum.
        driver = DRIVER_CS.read_text(encoding="utf-8")
        self.assertIn('Same(metric, "ramp_entry_progress_m")', driver)
        self.assertIn("min_progress=", driver)
        self.assertIn("maxRampEntryM", driver)          # latched maximum, not the final position
        self.assertIn("SKIPPED_NOT_APPLICABLE", driver)  # explicit skip, never a silent pass
        self.assertIn("CheckApplies(", driver)
        self.assertIn("notApplicable cell=", driver)
        doc = (ROOT / "Docs/Design/BallMotor/PHYSICS_TEST_FIELD.md").read_text(encoding="utf-8")
        for needle in ("### 4.1.4", "### 4.1.5", "### 4.1.6", "SKIPPED_NOT_APPLICABLE"):
            self.assertIn(needle, doc)

    def test_the_c01_timeline_covers_the_growth_derated_speed(self):
        # B-class fix: at D=2.2 growthSpeedFactor=0.25 caps the ball at 3.0 m/s, so the 6.86 m
        # ramp needs ~0.75 s more than the 4 m/s assumption allowed.
        c01 = self.cells["C01"]
        drive = c01["drive"]
        # The last row releases the second forward window; the row before it starts that window.
        self.assertGreaterEqual(drive[-1]["t"], 8.75)
        self.assertGreater(drive[-2]["forward"], 0.5)
        motor = MOTOR_CS.read_text(encoding="utf-8")
        self.assertIn("growthSpeedFactor = 0.25f", motor)


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
    if "--write-v1-snapshot" in sys.argv:
        write_v1_snapshot()
    else:
        unittest.main()
