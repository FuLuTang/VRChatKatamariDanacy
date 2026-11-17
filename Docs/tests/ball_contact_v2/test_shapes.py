"""Tests for the three shape layers (CONTACT_MODEL_V2.md §4A).

They pin the numbers and the ordering the design claims: the rolling shape is the
core union the pin tips (the visual core is the level's fixed start diameter and is
not computed by this module), pickup uses the same collision radius on every axis
and diagonal independently of rolling pins, and a pin can only protrude within the
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
)

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


class PickupShapeTests(unittest.TestCase):
    def test_pickup_is_the_collision_sphere_in_every_direction(self):
        for direction in ((1.0, 0.0, 0.0), (1.0, 1.0, 0.0), (1.0, 1.0, 1.0),
                          (0.0, 0.0, 0.0)):
            with self.subTest(direction=direction):
                self.assertEqual(pickup_reach(direction, 0.30), 0.30)

    def test_pickup_does_not_expand_with_the_rolling_pins(self):
        supports = [0.30] * NEEDLE_DIRECTION_COUNT
        # One needle protrudes through the cap (clamped to 0.405 for r=0.30).
        supports[0] = 1.0
        rolling = shape_with(supports)
        direction = rolling.directions[0]
        pickup = pickup_reach(direction, 0.30, rolling)
        self.assertEqual(pickup, 0.30)
        self.assertGreater(rolling.reach_along(direction), pickup)
        self.assertLessEqual(rolling.reach_along(direction), 0.405 + 1e-9)

    def test_pickup_radius_rejects_invalid_geometry(self):
        for radius in (-1.0, math.nan, math.inf):
            with self.subTest(radius=radius):
                with self.assertRaises(ValueError):
                    pickup_reach((1.0, 0.0, 0.0), radius)


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

    def test_every_pin_may_enter_the_contact_set_when_all_protrude(self):
        # Every attached prop registers into the pin table, so all 16 pins can protrude (many
        # tied at the cap); none may be dropped, and ties keep ascending index order.
        rolling = shape_with([0.405] * NEEDLE_DIRECTION_COUNT)
        self.assertEqual(COMPACT_NEEDLE_CAP, NEEDLE_DIRECTION_COUNT)
        self.assertEqual(rolling.protruding(), list(range(NEEDLE_DIRECTION_COUNT)))

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
