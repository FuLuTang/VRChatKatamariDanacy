"""from-layout: the author's ramp line is the *walking surface*, not the board's underside.

The layout (``Docs/Design/StoryMode/picnic_corner_layout.json``, design section 4.4)
gives each ramp a ``bottom`` and a ``top`` point and demands the top edge be flush with
the platform it arrives on (delta = 0).  P3 reads the ramp's +Y face for support, so the
+ Y face - the walking surface - has to land on those points and the 4 cm board hangs
below them.  The measured bug was the opposite: the board's underside sat on the line, so
the ball met the top face one thickness too high (``ramp_to_table`` 0.7346 vs the 0.70
table).  These tests pin both the heights and the validator allowance that makes the
lowered board legal.

The same file also pins the ``overview_3q`` framing: the camera that shipped cropped the
south-west corner (the corner tree and the stroller were out of frame).
"""

import json
import math
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Docs.tests.levelbuild.test_placement_core import valid_doc  # noqa: E402
from Tools.levelbuild import placement_core as core  # noqa: E402
from Tools.levelbuild.levelbuild import _structure_from_ramp  # noqa: E402

LAYOUT = ROOT / "Docs/Design/StoryMode/picnic_corner_layout.json"
PLACEMENT = ROOT / "Docs/Design/Levels/story_01_bench_size/placement.json"

#: The layout's numbers are metres at 4 decimals and the structure's centre is stored at
#: 4 decimals too, so a perfect rebuild still differs by ~7e-5 (rounding the centre y).
TOLERANCE_M = 1e-4


def load_ramps():
    layout = json.loads(LAYOUT.read_text(encoding="utf-8"))
    return layout, layout["ramps"]


class WalkingSurfaceTests(unittest.TestCase):
    def test_the_authored_line_is_the_walking_surface(self):
        _, ramps = load_ramps()
        for ramp in ramps:
            structure = _structure_from_ramp(ramp)
            normal, point = core.structure_top_plane(structure)
            top = core.plane_height_at(normal, point, ramp["top"][0], ramp["top"][2])
            foot = core.plane_height_at(normal, point, ramp["bottom"][0], ramp["bottom"][2])
            self.assertAlmostEqual(
                top, float(ramp["top"][1]), delta=TOLERANCE_M,
                msg="%s: walking surface at the top edge is %.5f, the layout says %.5f"
                    % (ramp["id"], top, ramp["top"][1]))
            self.assertAlmostEqual(
                foot, float(ramp["bottom"][1]), delta=TOLERANCE_M,
                msg="%s: walking surface at the foot is %.5f, the ground is %.5f"
                    % (ramp["id"], foot, ramp["bottom"][1]))

    def test_the_board_hangs_below_the_line(self):
        """Every board corner is on or under the walking plane, and the foot dips."""
        _, ramps = load_ramps()
        for ramp in ramps:
            structure = _structure_from_ramp(ramp)
            normal, point = core.structure_top_plane(structure)
            corners = core.box_corners(tuple(structure["center"]), tuple(structure["size"]),
                                       tuple(structure["rotationQuat"]))
            above = max(core.plane_distance(normal, point, corner) for corner in corners)
            self.assertLessEqual(above, 1e-6,
                                 "%s: a corner of the board is above the walking surface"
                                 % ramp["id"])
            self.assertLess(min(corner[1] for corner in corners), float(ramp["bottom"][1]),
                            "%s: the board should hang below its own foot, not sit on it"
                            % ramp["id"])

    def test_the_top_face_is_the_top_edge(self):
        """max corner y is what the 'meets' rule compares - it must be the design height."""
        _, ramps = load_ramps()
        for ramp in ramps:
            structure = _structure_from_ramp(ramp)
            corners = core.box_corners(tuple(structure["center"]), tuple(structure["size"]),
                                       tuple(structure["rotationQuat"]))
            self.assertAlmostEqual(max(corner[1] for corner in corners),
                                   float(ramp["top"][1]), delta=TOLERANCE_M)


class ValidatorAllowanceTests(unittest.TestCase):
    """The board hangs one thickness under the walking line, so its foot is under ground."""

    @staticmethod
    def doc_with_ramp(centre_y):
        doc = valid_doc()
        doc["structures"].append({
            "id": "plank", "role": "ramp", "shape": "box", "kind": "ramp",
            "center": [-2.0, centre_y, 0.0], "size": [1.0, core.RAMP_THICKNESS_M, 1.0],
            "rotationQuat": [0.0, 0.0, 0.0, 1.0], "collision": "box",
            "absorbable": False, "support": True})
        return doc

    def test_a_board_under_the_ground_by_one_thickness_is_green(self):
        # centre y = -0.01 -> underside at -0.03, inside RAMP_UNDERSIDE_ALLOWANCE_M
        doc = self.doc_with_ramp(-0.01)
        self.assertEqual(core.validate(doc).errors, [])

    def test_a_ramp_sunk_deeper_than_a_thickness_is_red(self):
        doc = self.doc_with_ramp(-0.04)     # underside at -0.06
        errors = core.validate(doc).errors
        self.assertTrue(any("sunk" in error for error in errors), errors)

    def test_the_shipped_level_is_green_with_its_lowered_ramps(self):
        report = core.validate(core.load_doc(PLACEMENT), path=PLACEMENT)
        self.assertEqual(report.errors, [])
        self.assertEqual(report.warnings, [])


class ShippedLevelTests(unittest.TestCase):
    """The generated placement must carry the same walking-surface heights as the layout."""

    def test_shipped_ramps_are_flush(self):
        layout, ramps = load_ramps()
        structures = {structure["id"]: structure
                      for structure in core.load_doc(PLACEMENT)["structures"]}
        for ramp in ramps:
            self.assertIn(ramp["id"], structures)
            normal, point = core.structure_top_plane(structures[ramp["id"]])
            top = core.plane_height_at(normal, point, ramp["top"][0], ramp["top"][2])
            foot = core.plane_height_at(normal, point, ramp["bottom"][0], ramp["bottom"][2])
            self.assertAlmostEqual(top, float(ramp["top"][1]), delta=TOLERANCE_M)
            self.assertAlmostEqual(foot, float(ramp["bottom"][1]), delta=TOLERANCE_M)

    def test_the_ramp_meets_its_platform_where_the_layout_says(self):
        """``ramp_to_bed`` ends on the flower-bed shell, at the same 0.26 m."""
        structures = {structure["id"]: structure
                      for structure in core.load_doc(PLACEMENT)["structures"]}
        self.assertEqual(structures["ramp_to_bed"].get("meets"), "flower_bed")
        ramp_top = max(corner[1] for corner in core.box_corners(
            tuple(structures["ramp_to_bed"]["center"]),
            tuple(structures["ramp_to_bed"]["size"]),
            tuple(structures["ramp_to_bed"]["rotationQuat"])))
        bed_top = core.structure_world_aabb(structures["flower_bed"])[1][1]
        self.assertAlmostEqual(ramp_top, bed_top, delta=TOLERANCE_M)
        # ramp_to_table ends on the folding table, which is a *prop* (no structure id to
        # name in "meets"), so only the height is assertable: the table's top is 0.70.
        table = next(prop for prop in core.load_doc(PLACEMENT)["props"]
                     if prop["itemId"] == "struct_folding_table_01")
        ramp_top = max(corner[1] for corner in core.box_corners(
            tuple(structures["ramp_to_table"]["center"]),
            tuple(structures["ramp_to_table"]["size"]),
            tuple(structures["ramp_to_table"]["rotationQuat"])))
        self.assertAlmostEqual(ramp_top, table["position"][1] + table["aabb"][1] / 2.0,
                               delta=TOLERANCE_M)


def project(camera, point, res=(1280, 720)):
    """Normalised camera coordinates, Blender's 36 mm sensor and auto fit (long edge).

    ``render_views.py`` renders the presets at the preset's resolution, which is where the
    sensor fit comes from; the same arithmetic is what decided the framing fix.
    """
    location, target = camera["location"], camera["target"]
    forward = [target[index] - location[index] for index in range(3)]
    length = math.sqrt(sum(value * value for value in forward))
    forward = [value / length for value in forward]
    right = [-forward[2], 0.0, forward[0]]
    norm = math.hypot(right[0], right[2])
    right = [right[0] / norm, 0.0, right[2] / norm]
    up = [right[1] * forward[2] - right[2] * forward[1],
          right[2] * forward[0] - right[0] * forward[2],
          right[0] * forward[1] - right[1] * forward[0]]
    tan_h = 18.0 / float(camera["lens"])
    tan_v = tan_h * float(res[1]) / float(res[0])
    delta = [point[index] - location[index] for index in range(3)]
    depth = sum(delta[index] * forward[index] for index in range(3))
    return (sum(delta[index] * right[index] for index in range(3)) / depth / tan_h,
            sum(delta[index] * up[index] for index in range(3)) / depth / tan_v)


class OverviewCameraTests(unittest.TestCase):
    def test_the_overview_keeps_the_whole_field_in_frame(self):
        doc = core.load_doc(PLACEMENT)
        camera = next(entry for entry in doc["cameras"] if entry["id"] == "overview_3q")
        (x0, z0), (x1, z1) = doc["level"]["bounds"]
        # Field corners at the ground and at the 1.2 m boundary-wall top.
        for x in (x0, x1):
            for z in (z0, z1):
                for y in (0.0, 1.2):
                    ndc_x, ndc_y = project(camera, (x, y, z))
                    self.assertLessEqual(max(abs(ndc_x), abs(ndc_y)), 0.95,
                                         "corner %r is outside the overview frame" % ((x, y, z),))


if __name__ == "__main__":
    unittest.main()
