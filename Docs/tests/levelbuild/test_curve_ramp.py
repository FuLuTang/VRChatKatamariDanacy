"""Tests for ``role=curve_ramp``: schema, expansion, round trip and the 45 deg gate.

The design is ``Docs/Design/BallMotor/CONTACT_MODEL_V2.md`` 1.5 / 3.10 / 5.7:

* a curved ramp is authored as ONE arc and expanded into a chain of overlapping slab
  segments, each at most one sagitta longer than its chord;
* the chord error must stay inside 5 % of the smallest ball radius (7.5 mm at the 0.30 m
  starter ball), which is what fixes the segment count;
* the expansion maths is the reference model's ``tessellate_vertical_curve`` - the tests here
  call that same function directly and compare, so a second tessellator cannot appear
  unnoticed;
* a ramp steeper than 45 deg is a WARNING now (v2 climbs it slowly), while values a solid
  board cannot have stay hard errors.

Run from the repository root: ``python3 -m unittest discover -s Docs/tests``.
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
from Tools.levelbuild.levelbuild import _structure_from_ramp  # noqa: E402
from Docs.tests.levelbuild.test_placement_core import valid_doc  # noqa: E402

#: Loading the 44 prop manifests per case is what makes a validation-heavy test slow; the
#: manifests are static data, so one load for the whole module is enough.
MANIFESTS = core.load_manifests()

#: A vertical arc that fits inside ``valid_doc``'s +-4 m bounds: 1.5 m radius, 30 deg, 0.6 m
#: wide, 0.1 m thick, leaving along +Z from the origin.
CURVE = {
    "radius_m": 1.5,
    "arc_deg": 30.0,
    "width_m": 0.6,
    "thickness_m": 0.1,
    "bend": "up",
}


def curve_structure(**overrides):
    structure = {
        "id": "curve_up_a",
        "role": "curve_ramp",
        "shape": "box",
        "collision": "box",
        "center": [-1.0, 0.0, 0.0],
        "rotationQuat": [0.0, 0.0, 0.0, 1.0],
        "curve": dict(CURVE),
    }
    curve = overrides.pop("curve", "keep")
    if curve is None:
        structure["curve"] = None
    elif curve != "keep":
        structure["curve"].update(curve)
    structure.update(overrides)
    return structure


def doc_with_curve(**overrides):
    doc = valid_doc()
    doc["structures"].append(curve_structure(**overrides))
    return doc


def add_ramp(doc, theta_deg, ramp_id="plank", length=1.0, width=1.0):
    """A straight ramp with its foot on the ground, built by the from-layout converter."""
    theta = math.radians(theta_deg)
    normal = (0.0, math.cos(theta), -math.sin(theta))
    tangent = (0.0, math.sin(theta), math.cos(theta))
    doc["structures"].append(_structure_from_ramp({
        "id": ramp_id, "normal": normal, "bottom": (0.0, 0.0, 0.0),
        "top": tuple(tangent[i] * length for i in range(3)),
        "slope_length": length, "width": width,
    }))
    return doc


def errors_of(doc):
    return core.validate(doc, manifests=MANIFESTS).errors


class CurveSchemaTests(unittest.TestCase):
    def test_a_valid_curve_passes_and_is_reported_as_one_ramp(self):
        doc = doc_with_curve()
        report = core.validate(doc, manifests=MANIFESTS)
        self.assertEqual(report.errors, [])
        self.assertEqual(report.warnings, [])
        self.assertEqual(report.tables["curveSummary"]["ramps"], 1)
        self.assertGreaterEqual(report.tables["curveSummary"]["segments"], 1)
        self.assertEqual(report.tables["curves"][0]["id"], "curve_up_a")

    def test_the_reason_codes_are_the_stable_part_of_the_message(self):
        cases = [
            ({"curve": None}, "curve_missing"),
            ({"curve": {"radius_m": 0.0}}, "curve_radius"),
            ({"curve": {"radius_m": float("nan")}}, "curve_radius"),
            ({"curve": {"arc_deg": 0.0}}, "curve_arc"),
            ({"curve": {"arc_deg": 361.0}}, "curve_arc"),
            ({"curve": {"width_m": 0.0}}, "curve_width"),
            ({"curve": {"thickness_m": 0.0}}, "curve_thickness"),
            ({"curve": {"bend": "left"}}, "curve_bend_unsupported"),
            ({"curve": {"bend": "helix"}}, "curve_bend_unsupported"),
            ({"curve": {"bend": "sideways"}}, "curve_bend"),
            ({"curve": {"segments": -1}}, "curve_segments"),
            ({"curve": {"segments": 100000}}, "curve_segments"),
            ({"curve": {"pitch_m": 2.4}}, "curve_unknown_field"),
            ({"curve": {"bank_deg": 10.0}}, "curve_unknown_field"),
            ({"size": [0.6, 0.1, 0.4]}, "curve_size"),
            ({"meets": "picnic_table"}, "curve_meets"),
            ({"shape": "plane"}, "curve_shape"),
            ({"collision": "none"}, "curve_collision"),
        ]
        for override, code in cases:
            with self.subTest(code=code, override=override):
                doc = doc_with_curve(**override)
                errors = errors_of(doc)
                self.assertTrue(errors, "the fixture was accepted but must be rejected")
                self.assertTrue(any("[" + code + "]" in error for error in errors),
                                "expected code %r in %r" % (code, errors))

    def test_an_explicit_segment_count_over_the_budget_is_red(self):
        # R = 1.5 m, 30 deg: 1 segment gives a 26 mm sagitta, far over the 7.5 mm budget.
        doc = doc_with_curve(curve={"segments": 1})
        errors = errors_of(doc)
        self.assertTrue(any("[curve_segments_coarse]" in error for error in errors), errors)
        # The message names the count that would fit (3 for this arc).
        self.assertIn("at least 3", " ".join(errors))

    def test_a_prop_cannot_be_seated_on_a_curve(self):
        doc = doc_with_curve()
        doc["props"][0]["support"] = "curve_up_a"
        errors = errors_of(doc)
        self.assertTrue(any("no single top plane" in error for error in errors), errors)

    def test_a_curve_is_derived_size_so_omitting_it_is_fine(self):
        doc = doc_with_curve()
        self.assertNotIn("size", doc["structures"][-1])
        self.assertEqual(errors_of(doc), [])
        text = core.dump_doc(doc)
        written = [s for s in json.loads(text)["structures"] if s["id"] == "curve_up_a"][0]
        self.assertEqual(written["size"], [0.6, 0.1, 0.0])


class CurveExpansionTests(unittest.TestCase):
    def test_auto_segment_counts_match_the_design_table(self):
        # CONTACT_MODEL_V2 5.7's table: 90 deg arcs, error budget 7.5 mm, r_min = 0.15 m.
        table = {2.0: 10, 3.0: 12, 5.0: 15, 10.0: 21, 20.0: 29}
        for radius, expected in table.items():
            with self.subTest(radius=radius):
                self.assertEqual(core.auto_curve_segments(radius, 90.0, 0.15), expected)

    def test_the_count_is_the_smallest_that_fits(self):
        for radius in (1.5, 4.0, 7.0, 12.0):
            count = core.auto_curve_segments(radius, 45.0, 0.15)
            reference = core.curve_reference()
            self.assertTrue(reference.curve_chord_budget(radius, count, 45.0, 0.15))
            if count > 1:
                self.assertFalse(reference.curve_chord_budget(radius, count - 1, 45.0, 0.15),
                                 "count-1 must be over budget, else the count is not minimal")

    def test_every_segment_sagitta_is_within_the_budget(self):
        for bend in ("up", "down"):
            structure = curve_structure(curve={"bend": bend, "arc_deg": 90.0, "radius_m": 4.0})
            chain, metrics = core.expand_curve_ramp(structure, 0.15)
            self.assertLessEqual(metrics["sagitta_m"], 0.05 * 0.15 + 1e-12, bend)
            self.assertGreater(metrics["sagitta_m"], 0.0)
            self.assertAlmostEqual(metrics["delta_deg"], 90.0 / len(chain), places=9)
            self.assertAlmostEqual(metrics["normal_jump_deg"], metrics["delta_deg"] / 2.0, places=9)

    def test_the_chain_follows_the_arc(self):
        structure = curve_structure(curve={"segments": 8, "arc_deg": 30.0, "radius_m": 4.0})
        chain, metrics = core.expand_curve_ramp(structure, 0.15)
        self.assertEqual(len(chain), 8)
        center = structure["center"]
        delta = math.radians(30.0 / 8)
        for index, segment in enumerate(chain):
            phi = (index + 0.5) * delta
            self.assertAlmostEqual(segment["center"][0], center[0], places=9)
            self.assertAlmostEqual(segment["center"][1], center[1] + 4.0 * (1 - math.cos(phi)),
                                   places=9)
            self.assertAlmostEqual(segment["center"][2], center[2] + 4.0 * math.sin(phi), places=9)
            # normal = -sin(phi) * forward + cos(phi) * up; tangent = cos(phi) * f + sin(phi) * u
            self.assertAlmostEqual(segment["normal"][1], math.cos(phi), places=9)
            self.assertAlmostEqual(segment["normal"][2], -math.sin(phi), places=9)
            self.assertAlmostEqual(segment["tangent"][1], math.sin(phi), places=9)
            self.assertAlmostEqual(segment["tangent"][2], math.cos(phi), places=9)

    def test_the_down_bend_descends_and_keeps_its_normal_up(self):
        structure = curve_structure(curve={"bend": "down", "segments": 8, "arc_deg": 30.0,
                                           "radius_m": 4.0})
        chain, _ = core.expand_curve_ramp(structure, 0.15)
        self.assertLess(chain[-1]["center"][1], chain[0]["center"][1])
        for segment in chain:
            self.assertGreater(segment["normal"][1], 0.0, "a down ramp still faces up")
            self.assertGreater(segment["normal"][2], 0.0, "and tilts towards travel")

    def test_down_is_the_mirror_of_up_at_the_same_segment_count(self):
        up, _ = core.expand_curve_ramp(curve_structure(curve={"segments": 10, "arc_deg": 45.0,
                                                             "radius_m": 5.0}), 0.15)
        down, _ = core.expand_curve_ramp(curve_structure(curve={"bend": "down", "segments": 10,
                                                               "arc_deg": 45.0,
                                                               "radius_m": 5.0}), 0.15)
        base = curve_structure()["center"]
        for index, (left, right) in enumerate(zip(up, down)):
            self.assertAlmostEqual(left["center"][1] - base[1], base[1] - right["center"][1],
                                   places=9, msg="segment %d" % index)
            self.assertAlmostEqual(left["center"][2], right["center"][2], places=9)
            self.assertAlmostEqual(left["normal"][1], right["normal"][1], places=9)
            self.assertAlmostEqual(left["normal"][2], -right["normal"][2], places=9)

    def test_the_frame_is_a_proper_rotation_with_local_y_up(self):
        chain, _ = core.expand_curve_ramp(curve_structure(curve={"segments": 5, "arc_deg": 40.0,
                                                                 "radius_m": 2.0}), 0.15)
        for segment in chain:
            quat = segment["rotationQuat"]
            for local, world in (((1.0, 0.0, 0.0), segment["axisX"]),
                                 ((0.0, 1.0, 0.0), segment["normal"]),
                                 ((0.0, 0.0, 1.0), segment["tangent"])):
                turned = core.quat_rotate(quat, local)
                self.assertAlmostEqual(max(abs(turned[i] - world[i]) for i in range(3)), 0.0,
                                       places=9)
            matrix = core.quat_to_matrix(quat)
            determinant = (matrix[0][0] * (matrix[1][1] * matrix[2][2] - matrix[1][2] * matrix[2][1])
                           - matrix[0][1] * (matrix[1][0] * matrix[2][2] - matrix[1][2] * matrix[2][0])
                           + matrix[0][2] * (matrix[1][0] * matrix[2][1] - matrix[1][1] * matrix[2][0]))
            self.assertAlmostEqual(determinant, 1.0, places=9, msg="a Unity transform is proper")

    def test_the_expansion_is_the_reference_tessellator_not_a_copy(self):
        structure = curve_structure(curve={"segments": 6, "arc_deg": 50.0, "radius_m": 3.0})
        chain, metrics = core.expand_curve_ramp(structure, 0.15)
        reference = core.curve_reference()
        self.assertEqual(Path(reference.__file__).resolve(), core.CURVE_REFERENCE_PATH)
        obbs, ref_metrics = reference.tessellate_vertical_curve(
            tuple(structure["center"]), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0), 3.0, 50.0, 6,
            0.6, 0.1)
        self.assertEqual(metrics["sagitta_m"], ref_metrics["sagitta_m"])
        for segment, obb in zip(chain, obbs):
            self.assertEqual(segment["center"], obb.center)
            self.assertEqual(segment["normal"], obb.axis_y)
            self.assertEqual(segment["tangent"], obb.axis_z)

    def test_expansion_is_deterministic(self):
        structure = curve_structure(curve={"arc_deg": 75.0, "radius_m": 2.5})
        first, first_metrics = core.expand_curve_ramp(structure, 0.15)
        second, second_metrics = core.expand_curve_ramp(copy.deepcopy(structure), 0.15)
        self.assertEqual(first, second)
        self.assertEqual(first_metrics, second_metrics)

    def test_the_normals_are_continuous_across_the_chain(self):
        # 5.7's criterion: the ball sees at most delta/2 of normal jump (it presses two
        # segments at once) and the chain is monotone - the normals turn one way only.
        structure = curve_structure(curve={"radius_m": 5.0, "arc_deg": 90.0})
        chain, metrics = core.expand_curve_ramp(structure, 0.15)
        delta = math.radians(metrics["delta_deg"])
        previous = None
        for index, segment in enumerate(chain):
            normal = segment["normal"]
            self.assertAlmostEqual(math.hypot(normal[1], normal[2]), 1.0, places=6)
            if previous is not None:
                angle = math.degrees(math.acos(max(-1.0, min(1.0,
                    sum(previous[i] * normal[i] for i in range(3))))))
                self.assertAlmostEqual(angle, math.degrees(delta), delta=1e-6,
                                       msg="segment %d" % index)
                # The design's tolerance is stated on what the BALL sees: it presses two
                # segments at once, so its normal jump is half a segment step.
                self.assertAlmostEqual(metrics["normal_jump_deg"], angle / 2.0, delta=1e-9)
            previous = normal

    def test_the_overlap_is_positive_and_under_a_quarter_of_a_chord(self):
        for radius in (1.0, 4.0, 20.0):
            chain, metrics = core.expand_curve_ramp(
                curve_structure(curve={"radius_m": radius, "arc_deg": 90.0}), 0.15)
            overlap = metrics["overlap"] - 1.0
            self.assertGreater(overlap, 0.0, "the convex side must not crack")
            self.assertLess(overlap, core.CURVE_MAX_OVERLAP_FRACTION)
            self.assertAlmostEqual(chain[0]["size"][2],
                                   metrics["segment_length_m"] * metrics["overlap"], places=9)


class CurveRoundTripTests(unittest.TestCase):
    def test_dump_load_dump_is_byte_identical_with_a_curve(self):
        doc = doc_with_curve(curve={"segments": 6})
        once = core.dump_doc(doc)
        twice = core.dump_doc(json.loads(once))
        self.assertEqual(once, twice)
        written = [s for s in json.loads(once)["structures"] if s["id"] == "curve_up_a"][0]
        self.assertEqual(written["curve"]["segments"], 6)
        self.assertEqual(written["size"], [0.6, 0.1, 0.0])

    def test_auto_segments_stay_auto(self):
        # Writing the resolved count back would turn a later radius edit into a stale
        # "too coarse" error instead of an automatic re-tessellation.
        text = core.dump_doc(doc_with_curve())
        written = [s for s in json.loads(text)["structures"] if s["id"] == "curve_up_a"][0]
        self.assertNotIn("segments", written["curve"])

    def test_a_curve_survives_a_rebuild_of_the_document(self):
        # The Blender/scene round trip is byte-identical for the documents we ship; here the
        # rebuild is the canonical pass (the Blender half needs a Blender run).
        doc = doc_with_curve(curve={"segments": 4, "bend": "down"})
        exported = core.dump_doc(doc)
        rebuilt = json.loads(exported)
        self.assertEqual(core.dump_doc(rebuilt), exported)
        curve = [s for s in rebuilt["structures"] if s["role"] == "curve_ramp"][0]
        self.assertEqual(curve["curve"]["bend"], "down")
        self.assertEqual(errors_of(rebuilt), [])


class CurveTotalsTests(unittest.TestCase):
    def test_a_curve_counts_once_in_the_ramp_budget_and_by_segments_as_obbs(self):
        doc = doc_with_curve()
        segments = core.auto_curve_segments(CURVE["radius_m"], CURVE["arc_deg"], 0.15)
        report = core.validate(doc, manifests=MANIFESTS)
        counts = report.tables["budget"]
        self.assertEqual(counts["counts"]["ramps"], 1, "one logical ramp")
        self.assertEqual(counts["curveRamps"], 1)
        self.assertEqual(counts["curveSegments"], segments)
        # 1 box (the table) + 0 straight ramps + the curve's segments.
        self.assertEqual(counts["staticObbs"], 1 + segments)

    def test_the_optional_expected_counters_are_checked(self):
        doc = doc_with_curve()
        segments = core.auto_curve_segments(CURVE["radius_m"], CURVE["arc_deg"], 0.15)
        doc["level"]["expected"] = {"propCount": 3, "valueUnits": 6,
                                    "structureCount": 3, "staticObbs": 1 + segments}
        self.assertEqual(errors_of(doc), [])

        doc["level"]["expected"]["staticObbs"] = 1 + segments + 1
        errors = errors_of(doc)
        self.assertTrue(any("staticObbs" in error for error in errors), errors)

        doc["level"]["expected"]["staticObbs"] = 1 + segments
        doc["level"]["expected"]["structureCount"] = 4      # a curve counts once
        errors = errors_of(doc)
        self.assertTrue(any("structureCount" in error for error in errors), errors)

    def test_the_static_obb_capacity_is_enforced(self):
        doc = valid_doc()
        for index in range(8):
            doc["structures"].append({
                "id": "box_%d" % index, "role": "box", "shape": "box",
                "center": [0.0, 0.1 + index, 0.0], "size": [0.1, 0.1, 0.1],
                "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "box",
            })
        doc["structures"].append(curve_structure(curve={"segments": 250, "radius_m": 20.0,
                                                        "arc_deg": 10.0}))
        errors = errors_of(doc)
        self.assertTrue(any("static OBBs" in error for error in errors), errors)


class RampGateTests(unittest.TestCase):
    """S4: the 45 deg ceiling is a warning; only impossible boards stay red."""

    def test_a_50_degree_ramp_is_a_warning_not_an_error(self):
        doc = add_ramp(valid_doc(), 50.0)
        report = core.validate(doc, manifests=MANIFESTS)
        self.assertEqual(report.errors, [])
        self.assertEqual(len(report.warnings), 1)
        self.assertIn("steeper than the 45 deg guideline", report.warnings[0])
        self.assertIn("climbs it slowly", report.warnings[0])

    def test_exactly_45_degrees_is_quiet(self):
        report = core.validate(add_ramp(valid_doc(), 45.0), manifests=MANIFESTS)
        self.assertEqual(report.errors, [])
        self.assertEqual(report.warnings, [])

    def test_an_89_degree_ramp_is_still_just_a_warning(self):
        report = core.validate(add_ramp(valid_doc(), 89.0), manifests=MANIFESTS)
        self.assertEqual(report.errors, [])
        self.assertEqual(len(report.warnings), 1)

    def test_a_vertical_board_is_red(self):
        report = core.validate(add_ramp(valid_doc(), 90.0), manifests=MANIFESTS)
        self.assertTrue(any("vertical" in error for error in report.errors), report.errors)

    def test_an_inverted_board_is_red(self):
        # Local +Y (the walking face) points down: not a rideable surface.
        doc = valid_doc()
        doc["structures"].append({
            "id": "inverted", "role": "ramp", "shape": "box",
            "center": [0.0, 0.2, 0.0], "size": [1.0, 0.04, 1.0],
            "rotationQuat": [1.0, 0.0, 0.0, 0.0], "collision": "box",
        })
        report = core.validate(doc, manifests=MANIFESTS)
        self.assertTrue(any("inverted" in error for error in report.errors), report.errors)

    def test_zero_thickness_and_nan_stay_red(self):
        doc = valid_doc()
        doc["structures"].append({
            "id": "paper", "role": "ramp", "shape": "box",
            "center": [0.0, 0.2, 0.0], "size": [1.0, 0.0, 1.0],
            "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "box",
        })
        self.assertTrue(any("positive on every axis" in error
                            for error in errors_of(doc)))

        doc = valid_doc()
        doc["structures"].append({
            "id": "nan", "role": "ramp", "shape": "box",
            "center": [0.0, float("nan"), 0.0], "size": [1.0, 0.04, 1.0],
            "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "box",
        })
        self.assertTrue(any("not finite" in error for error in errors_of(doc)))


if __name__ == "__main__":
    unittest.main()
