"""Tests for the three shape layers (CONTACT_MODEL_V2.md §4A).

They pin the numbers and the ordering the design claims: the visual core is
slightly inside the physical sphere, the rolling shape is the core union the pin
tips, the pickup reach is the patent's box support function (1.0 r on an axis to
1.73 r at a corner) unioned with the pins, and a pin can only protrude within the
same cap the radial proxy already uses.

Run with ``python3 -m unittest discover -s Docs/tests`` from the repo root.
"""
import math
import unittest

from Docs.Design.BallMotor.contact_v2.shapes import (
    COMPACT_NEEDLE_CAP,
    NEEDLE_DIRECTION_COUNT,
    RollingShape,
    grip_from_protrusion,
    mat3_from_axis_angle,
    needle_protrusion_cap,
    needle_tip_contact,
    pickup_reach,
    required_speed_with_grip,
    visual_core_radius,
)

ROOT3 = math.sqrt(3.0)


def fib_directions(count):
    """The same golden-angle set the radial proxy uses (ball-local, unit)."""
    directions = []
    for i in range(count):
        y = 1.0 - 2.0 * (i + 0.5) / count
        radius = math.sqrt(max(0.0, 1.0 - y * y))
        angle = i * math.pi * (3.0 - math.sqrt(5.0))
        directions.append((radius * math.cos(angle), y, radius * math.sin(angle)))
    return directions


def shape_with(supports, core=0.30):
    return RollingShape(core_radius=core, directions=fib_directions(len(supports)),
                        supports=supports)


class VisualShapeTests(unittest.TestCase):
    def test_visual_core_is_inside_the_physical_sphere(self):
        self.assertAlmostEqual(visual_core_radius(0.30), 0.285)
        self.assertAlmostEqual(visual_core_radius(1.10), 1.045)
        self.assertLess(visual_core_radius(1.10), 1.10)


class PickupShapeTests(unittest.TestCase):
    def test_box_reach_is_direction_dependent(self):
        # Axis 1.0 r, face diagonal sqrt(2) r, corner sqrt(3) r.
        self.assertAlmostEqual(pickup_reach((1.0, 0.0, 0.0), 0.30), 0.30)
        face = (1.0 / math.sqrt(2.0), 1.0 / math.sqrt(2.0), 0.0)
        self.assertAlmostEqual(pickup_reach(face, 0.30), 0.30 * math.sqrt(2.0))
        corner = (1.0 / ROOT3, 1.0 / ROOT3, 1.0 / ROOT3)
        self.assertAlmostEqual(pickup_reach(corner, 0.30), 0.30 * math.sqrt(3.0))

    def test_pickup_is_never_smaller_than_the_rolling_shape(self):
        supports = [0.30] * NEEDLE_DIRECTION_COUNT
        # One needle protrudes through the cap (clamped to 0.405 for r=0.30).
        supports[0] = 1.0
        rolling = shape_with(supports)
        direction = rolling.directions[0]
        pickup = pickup_reach(direction, 0.30, rolling)
        self.assertGreaterEqual(pickup, rolling.reach_along(direction) - 1e-12)
        self.assertLessEqual(rolling.reach_along(direction), 0.405 + 1e-9)


class RollingShapeTests(unittest.TestCase):
    def test_supports_are_clamped_to_the_proxy_cap(self):
        supports = [0.30] * NEEDLE_DIRECTION_COUNT
        supports[0] = 5.0
        rolling = shape_with(supports)
        # Cap = min(0.35*0.30, 0.30) = 0.105.
        self.assertAlmostEqual(rolling.supports[0], 0.405)
        self.assertAlmostEqual(needle_protrusion_cap(0.30), 0.105)
        self.assertAlmostEqual(needle_protrusion_cap(2.20), 0.30)

    def test_only_protruding_needles_enter_the_contact_set(self):
        supports = [0.30] * NEEDLE_DIRECTION_COUNT
        supports[2] = 0.40
        supports[5] = 0.42
        rolling = shape_with(supports)
        self.assertEqual(rolling.protruding()[:2], [5, 2])
        self.assertLessEqual(len(rolling.protruding()), COMPACT_NEEDLE_CAP)

    def test_tip_offsets_rotate_with_the_ball(self):
        rolling = shape_with([0.30] * NEEDLE_DIRECTION_COUNT)
        rolling.supports[0] = 0.40
        direction = rolling.directions[0]
        identity = mat3_from_axis_angle((0.0, 0.0, 1.0), 0.0)
        rotated = mat3_from_axis_angle((0.0, 0.0, 1.0), math.pi / 2.0)
        first = rolling.tip_offsets_world(identity)[0]
        second = rolling.tip_offsets_world(rotated)[0]
        # A 90 deg roll about +Z maps (x, y, z) to (-y, x, z).
        self.assertAlmostEqual(second[0], -first[1])
        self.assertAlmostEqual(second[1], first[0])
        self.assertAlmostEqual(second[2], first[2])

    def test_needle_tip_contact_reports_depth_and_normal(self):
        # A wide slab whose top face is y = 0; the tip is 5 mm above it.
        hit = needle_tip_contact((0.0, -1.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0),
                                 (0.0, 0.0, 1.0), (10.0, 1.0, 10.0),
                                 (0.2, 0.005, 0.3), 0.01)
        self.assertIsNotNone(hit)
        normal, depth, point = hit
        self.assertAlmostEqual(normal[1], 1.0)
        self.assertAlmostEqual(depth, 0.005)
        self.assertAlmostEqual(point[1], 0.0)
        # A tip 2 cm above the same face does not touch.
        self.assertIsNone(needle_tip_contact(
            (0.0, -1.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0),
            (10.0, 1.0, 10.0), (0.2, 0.025, 0.3), 0.01))


class GripTests(unittest.TestCase):
    def test_grip_halves_the_gate_at_a_half_radius_protrusion(self):
        self.assertAlmostEqual(grip_from_protrusion(0.0, 0.30), 0.0)
        self.assertAlmostEqual(grip_from_protrusion(0.15, 0.30), 0.5)
        self.assertAlmostEqual(grip_from_protrusion(0.40, 0.30), 0.5)  # clamped
        self.assertAlmostEqual(required_speed_with_grip(90.0, 0.15, 0.30), 0.75)
        self.assertAlmostEqual(required_speed_with_grip(90.0, 0.0, 0.30), 1.5)


if __name__ == "__main__":
    unittest.main()
