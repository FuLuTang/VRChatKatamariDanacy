"""Tests for the needle-tip SWEEP (CONTACT_MODEL_V2.md §4A.3).

The reference model's ``needle_tip_contact`` is a point test.  A rolling ball's pin
tip moves by ``v_center + omega x r``: at 4 m/s and r_roll = 0.15 the tip reaches
~16 m/s, so one 20 ms substep sweeps up to 0.32 m - farther than a thin wall.  A
point test tunnels through that, so production contact solving sweeps the segment
from the previous tip position to this one.  This file pins the reference
implementation of that sweep; the C# port (``BallShapeMath.TryNeedleSegmentContact``)
is asserted against the same numbers in the EditMode test
``NeedleSegmentTests.cs``, which is what keeps the two implementations from
drifting (the same discipline ``sweep.py`` <-> ``SweepMath.cs`` already uses).

Run with ``python3 -m unittest discover -s Docs/tests`` from the repo root.
"""
import math
import unittest

from Docs.Design.BallMotor.contact_v2.shapes import (
    needle_segment_contact,
    needle_segment_plane_contact,
    needle_tip_contact,
)

ROOT2 = math.sqrt(2.0)
ROOT3 = math.sqrt(3.0)

# The fixture box of the golden table: a 1 m cube centred on the origin, world axes.
BOX_CENTER = (0.0, 0.0, 0.0)
AXIS_X = (1.0, 0.0, 0.0)
AXIS_Y = (0.0, 1.0, 0.0)
AXIS_Z = (0.0, 0.0, 1.0)
HALF = (0.5, 0.5, 0.5)
TIP_RADIUS = 0.01


def sweep(previous, now):
    return needle_segment_contact(BOX_CENTER, AXIS_X, AXIS_Y, AXIS_Z, HALF,
                                  previous, now, TIP_RADIUS)


class FaceEntryTests(unittest.TestCase):
    def test_face_entry_is_found_at_the_inflated_face(self):
        # From x=2 to x=0 along the -x axis.  The tip (r = 0.01) first touches the
        # +x face at x = 0.51, i.e. fraction (2 - 0.51) / 2.
        normal, depth, point, fraction = sweep((2.0, 0.0, 0.0), (0.0, 0.0, 0.0))
        self.assertAlmostEqual(fraction, (2.0 - 0.51) / 2.0, places=6)
        self.assertAlmostEqual(depth, 0.0, places=5)
        self.assertEqual(normal, (1.0, 0.0, 0.0))
        self.assertAlmostEqual(point[0], 0.5, places=6)
        self.assertAlmostEqual(point[1], 0.0, places=6)

    def test_top_face_entry_uses_the_face_normal(self):
        # Straight down onto the +z face: the same 0.745 fraction, normal +z.
        normal, depth, point, fraction = sweep((0.5, 0.5, 2.0), (0.5, 0.5, 0.0))
        self.assertAlmostEqual(fraction, 0.745, places=6)
        self.assertAlmostEqual(depth, 0.0, places=5)
        self.assertEqual(normal, (0.0, 0.0, 1.0))
        self.assertAlmostEqual(point[2], 0.5, places=6)


class EdgeAndCornerTests(unittest.TestCase):
    def test_corner_pass_reports_the_corner_normal(self):
        # The body diagonal through the corner: the entry sample sits at the
        # inflated corner (sqrt(3) r away, no contact), the next sample is inside the
        # box, so the sweep bisects down to the first real touch.  The first touch is
        # where the tip centre is sqrt(3) r from the corner, i.e.
        # k = 0.5 + 0.01/sqrt(3) along the diagonal, fraction (1 - k) / 2.
        normal, depth, point, fraction = sweep((1.0, 1.0, 1.0), (-1.0, -1.0, -1.0))
        expected = (1.0 - (0.5 + TIP_RADIUS / ROOT3)) / 2.0
        self.assertAlmostEqual(fraction, expected, places=3)
        self.assertLess(depth, 1e-4, "the first touch has (almost) zero depth")
        for component in normal:
            self.assertAlmostEqual(component, 1.0 / ROOT3, places=5)
        for component in point:
            self.assertAlmostEqual(component, 0.5, places=5)

    def test_a_pass_outside_the_box_is_not_a_contact(self):
        self.assertIsNone(sweep((2.0, 2.0, 2.0), (2.0, 2.0, 0.0)))

    def test_a_tip_that_starts_inside_is_pushed_out_through_the_nearest_face(self):
        normal, depth, point, fraction = sweep((0.0, 0.0, 0.0), (0.0, 0.0, 0.0))
        self.assertEqual(fraction, 0.0)
        self.assertEqual(normal, (1.0, 0.0, 0.0))
        self.assertAlmostEqual(depth, TIP_RADIUS + 0.5, places=6)
        self.assertAlmostEqual(point[0], 0.5, places=6)


class DegenerateSegmentTests(unittest.TestCase):
    def test_a_still_tip_matches_the_point_test(self):
        # The sweep must degrade to the point test when the tip does not move: that
        # is the reference model's static form, and it is the invariant the C# port
        # keeps so a ball standing still cannot miss a pin contact.
        for point in [(0.505, 0.5, 0.5), (0.5, 0.5, 0.6), (0.0, 0.0, 0.0),
                      (0.51, 0.51, 0.5), (2.0, 0.0, 0.0)]:
            swept = sweep(point, point)
            static = needle_tip_contact(BOX_CENTER, AXIS_X, AXIS_Y, AXIS_Z, HALF,
                                        point, TIP_RADIUS)
            if static is None:
                self.assertIsNone(swept, "still tip at " + str(point))
                continue
            self.assertIsNotNone(swept, "still tip at " + str(point))
            normal, depth, closest, fraction = swept
            static_normal, static_depth, static_closest = static
            self.assertEqual(fraction, 0.0)
            self.assertAlmostEqual(depth, static_depth, places=6)
            for i in range(3):
                self.assertAlmostEqual(normal[i], static_normal[i], places=6)
                self.assertAlmostEqual(closest[i], static_closest[i], places=6)

    def test_a_still_tip_just_outside_the_skin_is_not_a_contact(self):
        self.assertIsNone(sweep((0.52, 0.5, 0.5), (0.52, 0.5, 0.5)))
        normal, depth, _, _ = sweep((0.505, 0.5, 0.5), (0.505, 0.5, 0.5))
        self.assertEqual(normal, (1.0, 0.0, 0.0))
        self.assertAlmostEqual(depth, 0.005, places=6)


class RotatedBoxTests(unittest.TestCase):
    def test_a_rotated_box_is_swept_in_its_own_frame(self):
        # 45 deg about +Y, so the world +x extreme is the corner at (1/sqrt(2), 0, 0).
        # A tip travelling along -x at y = z = 0 touches that corner when
        # x = 1/sqrt(2) + r, i.e. fraction (1.5 - (1/sqrt(2) + 0.01)) / 3.
        half = 1.0 / ROOT2
        axis_x = (half, 0.0, -half)
        axis_z = (half, 0.0, half)
        normal, depth, point, fraction = needle_segment_contact(
            BOX_CENTER, axis_x, AXIS_Y, axis_z, HALF, (1.5, 0.0, 0.0), (-1.5, 0.0, 0.0),
            TIP_RADIUS)
        expected = (1.5 - (half + TIP_RADIUS)) / 3.0
        self.assertAlmostEqual(fraction, expected, places=3)
        self.assertAlmostEqual(depth, 0.0, places=4)
        self.assertAlmostEqual(normal[0], 1.0, places=4)
        self.assertAlmostEqual(point[0], half, places=5)


class PlaneSweepTests(unittest.TestCase):
    def test_plane_entry_is_found_at_tip_radius(self):
        normal, depth, point, fraction = needle_segment_plane_contact(
            (0.0, 1.0, 0.0), 0.0, (0.0, 2.0, 0.0), (0.0, -1.0, 0.0), TIP_RADIUS)
        self.assertAlmostEqual(fraction, (2.0 - TIP_RADIUS) / 3.0, places=6)
        self.assertAlmostEqual(depth, 0.0, places=6)
        self.assertEqual(normal, (0.0, 1.0, 0.0))
        self.assertAlmostEqual(point[1], 0.0, places=6)

    def test_a_tip_already_below_the_surface_reports_its_penetration(self):
        normal, depth, point, fraction = needle_segment_plane_contact(
            (0.0, 1.0, 0.0), 0.0, (0.0, 0.005, 0.0), (0.0, -0.5, 0.0), TIP_RADIUS)
        self.assertEqual(fraction, 0.0)
        self.assertAlmostEqual(depth, 0.005, places=6)
        self.assertEqual(normal, (0.0, 1.0, 0.0))

    def test_a_tip_that_stops_above_the_surface_is_not_a_contact(self):
        self.assertIsNone(needle_segment_plane_contact(
            (0.0, 1.0, 0.0), 0.0, (0.0, 2.0, 0.0), (0.0, 0.5, 0.0), TIP_RADIUS))


class ThinWallTests(unittest.TestCase):
    def test_a_sweep_crosses_a_wall_thinner_than_the_step(self):
        # The reason the segment exists: a 0.02 m thick wall (half extent 0.01 along
        # x) with a tip swept 0.32 m through it.  A point test at either end sees
        # nothing; the sweep reports the entrance.
        thin = (0.01, 2.0, 2.0)
        normal, depth, point, fraction = needle_segment_contact(
            BOX_CENTER, AXIS_X, AXIS_Y, AXIS_Z, thin, (0.16, 0.0, 0.0), (-0.16, 0.0, 0.0),
            TIP_RADIUS)
        self.assertAlmostEqual(fraction, (0.16 - (0.01 + TIP_RADIUS)) / 0.32, places=6)
        self.assertAlmostEqual(depth, 0.0, places=5)
        self.assertEqual(normal, (1.0, 0.0, 0.0))
        self.assertIsNone(needle_tip_contact(BOX_CENTER, AXIS_X, AXIS_Y, AXIS_Z, thin,
                                             (0.16, 0.0, 0.0), TIP_RADIUS))
        self.assertIsNone(needle_tip_contact(BOX_CENTER, AXIS_X, AXIS_Y, AXIS_Z, thin,
                                             (-0.16, 0.0, 0.0), TIP_RADIUS))


if __name__ == "__main__":
    unittest.main()
