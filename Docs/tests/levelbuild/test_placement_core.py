"""Tests for the level authoring data layer (no Blender, no Unity).

Run from the repository root:

    python3 -m unittest discover -s Docs/tests -p 'test_*.py'

The rules under test are the ones that fail *silently* if they are wrong -
coordinate conversion, quaternion order, the absorb gate, seating on a tilted
surface, and the deterministic byte layout - so each has a numeric test rather
than a "looks fine" check.
"""

import copy
import json
import math
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.levelbuild import placement_core as core  # noqa: E402

EXAMPLE = ROOT / "Tools/levelbuild/examples/placement.json"
MANIFESTS = core.load_manifests()


def manifest_aabb(def_id):
    return list(MANIFESTS[def_id]["aabb"])


def has_negative_zero(node):
    """True when any float in the document is a negative zero (it would print as -0.0)."""
    if isinstance(node, float):
        return node == 0.0 and math.copysign(1.0, node) < 0.0
    if isinstance(node, dict):
        return any(has_negative_zero(value) for value in node.values())
    if isinstance(node, list):
        return any(has_negative_zero(value) for value in node)
    return False


def valid_doc():
    """A minimal document that must pass every rule; the tests mutate copies of it."""
    table_top = 0.425
    plate_aabb = manifest_aabb("plate")
    return {
        "schema": core.SCHEMA,
        "level": {
            "id": "unit_test_field",
            "scene": "TestScene",
            "units": "m",
            "convention": core.CONVENTION,
            "ballStart": [-2.5, 0.15, -2.5],
            "playerSpawn": [-3.6, 0.5, -3.6],
            "ballDiameterStart": 0.30,
            "ballDiameterMax": 2.20,
            "bounds": [[-4.0, -4.0], [4.0, 4.0]],
            "spawnClearanceM": 1.0,
            "budget": {"planes": 8, "boxes": 8, "ramps": 4, "propSupports": 6, "blocking": 6},
            "expected": {"propCount": 3, "valueUnits": 6},
        },
        "defs": [
            {"id": "apple", "kind": "fruit", "valueUnits": 1},
            {"id": "orange", "kind": "fruit", "valueUnits": 1},
            {"id": "plate", "kind": "tableware", "valueUnits": 4},
        ],
        "props": [
            {"itemId": "prop_apple_000", "def": "apple", "zone": "lawn",
             "position": [0.6, 0.0387, 0.6], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
             "aabb": manifest_aabb("apple"), "valueUnits": 1},
            {"itemId": "prop_orange_000", "def": "orange", "zone": "lawn",
             "position": [-1.2, 0.0387, 0.9], "rotationQuat": [0.0, 0.0, 0.0, 1.0],
             "aabb": manifest_aabb("orange"), "valueUnits": 1},
            {"itemId": "prop_plate_000", "def": "plate", "zone": "table",
             "position": [1.5, table_top + plate_aabb[1] / 2.0, -1.5],
             "rotationQuat": [0.0, 0.0, 0.0, 1.0], "aabb": plate_aabb,
             "support": "picnic_table", "valueUnits": 4},
        ],
        "structures": [
            {"id": "ground", "role": "ground", "shape": "plane",
             "center": [0.0, 0.0, 0.0], "size": [8.0, 0.04, 8.0],
             "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "none",
             "absorbable": False, "support": True},
            {"id": "picnic_table", "role": "box", "shape": "box",
             "center": [1.5, 0.4, -1.5], "size": [1.2, 0.05, 0.8],
             "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "box",
             "absorbable": False, "support": True},
        ],
    }


class CoordinateTests(unittest.TestCase):
    def test_position_swap_is_its_own_inverse(self):
        for point in ((1.0, 2.0, 3.0), (-0.5, 0.0, 4.25)):
            self.assertEqual(core.blender_to_unity_position(point), (point[0], point[2], point[1]))
            self.assertEqual(core.unity_to_blender_position(
                core.blender_to_unity_position(point)), point)

    def test_blender_z_rotation_becomes_unity_negative_y_rotation(self):
        # A quarter turn about Blender +Z is a quarter turn about Unity +Y the other way
        # (the axis swap is improper). Unity's +Y yaw turns forward (+Z) towards +X.
        q_blender = core.quat_axis_angle((0.0, 0.0, 1.0), math.radians(90.0))
        q_unity = core.blender_to_unity_quat(
            (q_blender[3], q_blender[0], q_blender[1], q_blender[2]))
        forward = core.quat_rotate(q_unity, (0.0, 0.0, 1.0))
        self.assertAlmostEqual(forward[0], -1.0, places=6)
        self.assertAlmostEqual(forward[1], 0.0, places=6)
        self.assertAlmostEqual(forward[2], 0.0, places=6)

    def test_quaternion_round_trip(self):
        q = core.canonical_quat(core.quat_axis_angle((0.3, 0.5, -0.8), 0.7))
        blender = core.unity_to_blender_quat(q)
        back = core.blender_to_unity_quat(blender)
        self.assertAlmostEqual(max(abs(a - b) for a, b in zip(q, back)), 0.0, places=12)

    def test_canonical_sign_is_stable(self):
        q = core.quat_axis_angle((0.0, 1.0, 0.0), math.radians(200.0))
        negated = tuple(-v for v in q)
        self.assertEqual(core.canonical_quat(q), core.canonical_quat(negated))

    def test_rotated_world_aabb(self):
        # A 0.24 x 0.025 x 0.24 plate yawed 45 deg has a wider world footprint.
        q = core.quat_axis_angle((0.0, 1.0, 0.0), math.radians(45.0))
        half = core.world_half_extents((0.24, 0.025, 0.24), q)
        self.assertAlmostEqual(half[0], 0.24 * math.sqrt(0.5), places=6)
        self.assertAlmostEqual(half[1], 0.0125, places=6)
        self.assertAlmostEqual(half[2], 0.24 * math.sqrt(0.5), places=6)


class AbsorbGateTests(unittest.TestCase):
    """The absorb gate: the ORIGINAL numbers (k x bbox, 10x volume, floor to mm).

    Two quantities are tested separately on purpose: the continuous D_req
    (``absorb_diameter_req_exact``) is the physics, and the whole-millimetre ``Req``
    (``absorb_diameter_req``) is what the original compares.  Every number here is
    recomputed from the formula in the test, not copied from the implementation.
    """

    def test_formula_matches_the_original_rule(self):
        size = (0.08, 0.078, 0.0789)
        for k in (core.DEFAULT_VOLUME_K, 0.5, 1.0, 2.0, 8.0):
            expected = (60.0 * k * size[0] * size[1] * size[2] / math.pi) ** (1.0 / 3.0)
            self.assertAlmostEqual(core.absorb_diameter_req_exact(size, k), expected, places=12)
        # The default is the original's 0.70, so an undeclared def gets k=0.70.
        self.assertEqual(core.DEFAULT_VOLUME_K, 0.70)

    def test_req_is_floored_to_whole_millimetres(self):
        size = (0.08, 0.078, 0.0789)
        exact_mm = core.absorb_diameter_req_exact(size) * 1000.0
        self.assertEqual(core.absorb_diameter_req_mm(size), int(math.floor(exact_mm)))
        self.assertAlmostEqual(core.absorb_diameter_req(size),
                               core.absorb_diameter_req_mm(size) / 1000.0, places=12)
        # The stored value is exactly three decimals (that is why the field is 0.187,
        # not 0.1874): a floored mm is a whole number of millimetres.
        self.assertAlmostEqual(core.absorb_diameter_req(size) * 1000.0,
                               round(core.absorb_diameter_req(size) * 1000.0), places=9)

    def test_known_values(self):
        # apple: k=0.70 gives 187.4 mm -> 187 mm (was 211 mm under the k-less gate).
        self.assertAlmostEqual(core.absorb_diameter_req(manifest_aabb("apple")), 0.187, places=3)
        self.assertAlmostEqual(core.absorb_diameter_req(manifest_aabb("banana")), 0.173, places=3)
        self.assertAlmostEqual(core.absorb_diameter_req(manifest_aabb("thermos")), 0.358, places=3)
        self.assertAlmostEqual(core.absorb_diameter_req(manifest_aabb("plate")), 0.268, places=3)
        self.assertAlmostEqual(core.absorb_diameter_req(manifest_aabb("watermelon")), 0.596, places=3)
        self.assertAlmostEqual(core.absorb_diameter_req(manifest_aabb("napkin")), 0.161, places=3)

    def test_the_shared_numeric_contract(self):
        # 60 = 6 * 10 (the 10x gate), 0.70 the original default factor, 1e-3 mm the boundary
        # guard the C# twin (PickupAbsorbMath.MmFloorEpsilon) uses. These are the three
        # numbers the two implementations must agree on; the C# file spells them in its own
        # constants and this assertion is the Python half of that contract.
        self.assertEqual(60.0, core.VOLUME_GATE_FACTOR)
        self.assertEqual(0.70, core.DEFAULT_VOLUME_K)
        self.assertEqual(1e-3, core.MM_EPSILON)
        self.assertEqual(32.0, core.VOLUME_K_MAX)

    def test_k_multiplies_the_volume_not_the_length(self):
        size = manifest_aabb("watermelon")
        base = core.absorb_diameter_req_exact(size, 1.0)
        for k in (0.70, 0.5, 2.0):
            self.assertAlmostEqual(core.absorb_diameter_req_exact(size, k) / base,
                                   k ** (1.0 / 3.0), places=9)

    def test_gate_is_the_volume_rule(self):
        for def_id in ("apple", "banana", "thermos", "plate", "watermelon", "napkin"):
            size = manifest_aabb(def_id)
            req_m = core.absorb_diameter_req(size)
            volume = core.DEFAULT_VOLUME_K * size[0] * size[1] * size[2]
            # The original FLOORS Req, so the accepted ball is up to 1 mm smaller than the
            # exact 10x requirement ("the required pickup size is generally a little bit
            # less than 10 times bigger"): volume is at or above the 10x volume of Req and
            # strictly below the 10x volume of Req + 1 mm.
            self.assertGreaterEqual(volume, (math.pi / 6.0) * req_m ** 3 / 10.0 - 1e-12)
            above = (core.absorb_diameter_req_mm(size) + 1) / 1000.0
            self.assertLess(volume, (math.pi / 6.0) * above ** 3 / 10.0)

    def test_absorbable_flips_on_the_whole_millimetre_boundary(self):
        size = manifest_aabb("watermelon")
        req_m = core.absorb_diameter_req(size)
        self.assertTrue(core.is_absorbable(size, req_m))
        self.assertFalse(core.is_absorbable(size, req_m - 0.001))
        # A ball diameter that is only float-noise above the boundary still passes.
        self.assertTrue(core.is_absorbable(size, req_m + 1e-9))

    def test_epsilon_lifts_an_exact_boundary_back_onto_its_millimetre(self):
        # A 0.3 m cube scaled by the gate's own constants lands on an exact 300 mm
        # boundary; the product of three floats lands a few ulps below it.
        side = (300.0 / 1000.0) * (math.pi / (60.0 * core.DEFAULT_VOLUME_K)) ** (1.0 / 3.0)
        exact_mm = core.absorb_diameter_req_exact((side, side, side)) * 1000.0
        self.assertAlmostEqual(exact_mm, 300.0, places=6)
        self.assertEqual(core.absorb_diameter_req_mm((side, side, side)), 300)
        self.assertTrue(core.is_absorbable((side, side, side), 0.300))

    def test_volume_k_defaults_and_overrides(self):
        catalog = {"apple": {"id": "apple", "volumeK": 2.0}}
        self.assertEqual(core.effective_volume_k({"def": "apple"}, catalog), 2.0)
        self.assertEqual(core.effective_volume_k({"def": "apple", "volumeK": 0.5}, catalog), 0.5)
        self.assertEqual(core.effective_volume_k({"def": "unknown"}, catalog),
                         core.DEFAULT_VOLUME_K)

    def test_volume_k_range_is_the_one_authoring_rule(self):
        for bad in (0, -1, 33, 70, float("nan"), float("inf"), "0.7", True):
            self.assertIsNotNone(core.volume_k_error(bad), bad)
        for good in (0.01, 0.5, 0.7, 1.0, 16.0, 32.0):
            self.assertIsNone(core.volume_k_error(good), good)

    def test_default_k_is_never_written_into_a_document(self):
        doc = valid_doc()
        text = core.dump_doc(doc)
        self.assertNotIn("volumeK", text)
        doc["defs"][0]["volumeK"] = 0.5
        text = core.dump_doc(doc)
        self.assertIn('"volumeK": 0.5', text)
        # A prop override is emitted too, and the def-level value prices the gate.
        doc["defs"][0]["volumeK"] = 2.0
        doc["props"][0]["volumeK"] = 0.5
        text = core.dump_doc(doc)
        self.assertIn('"volumeK": 2.0', text)
        self.assertEqual(core.dump_doc(json.loads(text)), text)


class SeatingTests(unittest.TestCase):
    def setUp(self):
        self.doc = valid_doc()
        self.ramp = {"id": "plank", "role": "ramp", "shape": "box",
                     "center": [-0.5, 0.2, 0.0], "size": [1.1, 0.04, 0.42],
                     "rotationQuat": list(core.quat_axis_angle((0.0, 0.0, 1.0),
                                                               math.radians(20.0))),
                     "collision": "box", "absorbable": False, "support": True}

    def test_seated_prop_is_level_and_sunk_prop_is_not(self):
        size = manifest_aabb("apple")
        normal, point = core.structure_top_plane(self.ramp)
        position = (0.0, 0.3, 0.0)
        seated, quat = core.seat_pose(position, size, normal, point, yaw_rad=0.0)
        corners = core.box_corners(seated, size, quat)
        self.assertAlmostEqual(min(core.plane_distance(normal, point, c) for c in corners), 0.0, places=6)
        # An upright prop pushed down to touch the plane by its AABB must have a corner under it.
        upright = (seated[0], seated[1], seated[2])
        upright = core.seat_position(position, size, normal, point)
        flat = core.box_corners(upright, size, (0.0, 0.0, 0.0, 1.0))
        self.assertLess(min(core.plane_distance(normal, point, c) for c in flat), -0.01)

    def test_reseat_keeps_the_prop_on_its_support(self):
        doc = copy.deepcopy(self.doc)
        doc["structures"].append(self.ramp)
        size = manifest_aabb("apple")
        normal, point = core.structure_top_plane(self.ramp)
        doc["props"].append({
            "itemId": "prop_apple_001", "def": "apple", "zone": "ramp",
            "position": [core.rounded(0.0, 4), core.rounded(0.3, 4), 0.0],
            "rotationQuat": [0.0, 0.0, 0.0, 1.0], "aabb": size,
            "support": "plank", "valueUnits": 1,
        })
        doc["level"]["expected"] = {"propCount": 4, "valueUnits": 7}
        self.assertTrue(core.validate(doc).errors)  # upright on a 20 deg board = sunk corner

        prop = doc["props"][-1]
        new_position, new_quat = core.reseat(tuple(prop["position"]), tuple(prop["aabb"]),
                                            tuple(prop["rotationQuat"]), normal, point)
        prop["position"] = [core.rounded(v, core.POSITION_DECIMALS) for v in new_position]
        prop["rotationQuat"] = [core.rounded(v, core.QUAT_DECIMALS) for v in new_quat]
        report = core.validate(doc)
        self.assertEqual(report.errors, [])


class DeterminismTests(unittest.TestCase):
    def test_canonical_form_is_sorted_rounded_and_clean(self):
        doc = valid_doc()
        doc["props"].reverse()
        doc["structures"].reverse()
        doc["props"][0]["position"] = [-0.0, 0.0387, 1.23456]
        text = core.dump_doc(doc)
        self.assertFalse(has_negative_zero(json.loads(text)), "a -0.0 survived the canonical pass")
        self.assertIn("1.2346", text)
        ids = [line for line in text.splitlines() if '"itemId"' in line]
        self.assertEqual(ids, sorted(ids))

    def test_derived_fields_are_recomputed(self):
        doc = valid_doc()
        doc["props"][0]["absorbable"] = False
        doc["props"][0]["absorbDiameterReq"] = 9.999
        text = core.dump_doc(doc)
        self.assertNotIn("9.999", text)
        # apple: k=0.70 prices it at 187 mm, so the stale 9.999 cannot survive.
        self.assertIn('"absorbDiameterReq": 0.187', text)
        self.assertIn('"absorbable": true', text)

    def test_dump_is_byte_stable_through_a_round_trip(self):
        doc = valid_doc()
        once = core.dump_doc(doc)
        twice = core.dump_doc(json.loads(once))
        self.assertEqual(once, twice)

    def test_example_is_canonical(self):
        doc = core.load_doc(EXAMPLE)
        self.assertEqual(core.dump_doc(doc), EXAMPLE.read_text(encoding="utf-8"))

    def test_value_units_fall_back_to_the_def_catalog(self):
        doc = valid_doc()
        for prop in doc["props"]:
            prop.pop("valueUnits", None)
        self.assertEqual(core.value_units_total(doc), 6)
        self.assertEqual(core.validate(doc).errors, [])


class ValueUnitsRangeTests(unittest.TestCase):
    """valueUnits is a JSON integer with no float conversion, past int32.

    The Unity half of Docs/OPEN_DECISIONS.md (r) step B widened the host column to
    ``long[]``; Python's ``int`` was already unbounded, so the only way this side can
    regress is a float creeping into the emitted JSON or into a total. A value past
    int32's 2,147,483,647 is used so the retired 32-bit ceiling cannot come back
    unnoticed. The unit and every stored number are unchanged by step B, so the fixture's
    other values stay as they are.
    """

    PAST_INT_MAX = 3000000000

    def big_doc(self):
        doc = valid_doc()
        doc["defs"][0]["valueUnits"] = self.PAST_INT_MAX
        doc["props"][0]["valueUnits"] = self.PAST_INT_MAX
        # prop_apple_000 overrides its def, so the total is 3e9 + 1 (orange) + 4 (plate).
        doc["level"]["expected"]["valueUnits"] = self.PAST_INT_MAX + 1 + 4
        return doc

    def test_a_value_past_int32_stays_an_integer_in_the_json(self):
        doc = self.big_doc()
        canonical = core.canonical_doc(doc)
        self.assertIs(type(canonical["defs"][0]["valueUnits"]), int)
        self.assertIs(type(canonical["props"][0]["valueUnits"]), int)
        self.assertEqual(canonical["defs"][0]["valueUnits"], self.PAST_INT_MAX)
        self.assertEqual(canonical["props"][0]["valueUnits"], self.PAST_INT_MAX)

        text = core.dump_doc(doc)
        self.assertIn('"valueUnits": %d' % self.PAST_INT_MAX, text)
        self.assertNotIn("%d.0" % self.PAST_INT_MAX, text)
        self.assertNotIn("3e+09", text.lower())

        back = json.loads(text)
        self.assertIs(type(back["defs"][0]["valueUnits"]), int)
        self.assertIs(type(back["props"][0]["valueUnits"]), int)
        self.assertEqual(back["props"][0]["valueUnits"], self.PAST_INT_MAX)

    def test_totals_stay_exact_past_int32(self):
        doc = self.big_doc()
        expected = self.PAST_INT_MAX + 1 + 4
        total = core.value_units_total(doc)
        self.assertIs(type(total), int)
        self.assertEqual(total, expected)
        self.assertEqual(core.expected_totals(doc)["valueUnits"], expected)
        self.assertIs(type(core.expected_totals(doc)["valueUnits"]), int)

        report = core.validate(doc)
        self.assertEqual(report.errors, [])
        self.assertEqual(report.warnings, [])

    def test_a_fractional_value_is_refused_not_rounded(self):
        # The mirror of the editor's whole-unit rule (Docs/OPEN_DECISIONS.md (j)): a float
        # in the file is a data error, not something to truncate on the way to the host.
        doc = valid_doc()
        doc["props"][0]["valueUnits"] = 1.5
        report = core.validate(doc)
        self.assertTrue(any("valueUnits must be a whole number" in error
                            for error in report.errors), report.errors)


if __name__ == "__main__":
    unittest.main()
