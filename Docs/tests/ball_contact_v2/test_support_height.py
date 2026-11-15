"""Support height under a resting ball (CONTACT_MODEL_V2.md §2.5, §7.5.4).

Why this file exists: the contact v2 first run (pass6, 2026-11-15) failed the test
field's ``rest_gap`` check on exactly the cells where the ball had climbed onto a
step or a box top, reading ``support_gap_m = -radius``.  The motor computes

    support_gap = (center.y - radius) - support_height

so a value of ``-radius`` means ``support_height == center.y``.  This file pins the
two halves of that story against the reference model:

1. the height formula of §2.5 (``center.y - dot(n, center - point) / n.y``) gives
   the box TOP for a ball resting on it, at every shipped diameter, so a resting
   ball's gap is 0 - the formula and the write-back were not the problem;
2. a VERTICAL wall contact's point sits at the ball's own height and its normal has
   no +Y part, so the §2.5 formula is undefined there and using the point's y as a
   floor height reads exactly -radius.  That is why the motor's climb branch must
   report the ball's bottom instead of the wall point, and why a climb has to END
   when its gate closes instead of hovering on the wall.

Run with ``python3 -m unittest discover -s Docs/tests`` from the repo root.
"""
import unittest

from Docs.Design.BallMotor.contact_v2.contact_v2 import (
    Obb,
    Plane,
    ROLE_BLOCKING,
    ROLE_BOX,
    State,
    World,
    sphere_obb_contact,
    step,
)

DT = 0.02
# The diameters the test field runs.
DIAMETERS = (0.30, 0.60, 1.10, 2.20)
# The step heights pass6 failed on (C08 = 0.05, C09 = 0.075, C10 = 0.10).
TOPS = (0.05, 0.075, 0.10)
GROUND = Plane((0.0, 1.0, 0.0), 0.0)
SKIN = 1e-6


def unit_box(top, half_width=2.0):
    """A wide box whose +Y face is at ``top`` (the cell's step/deck)."""
    return Obb(center=(0.0, top * 0.5, 0.0), axis_x=(1.0, 0.0, 0.0),
               axis_y=(0.0, 1.0, 0.0), axis_z=(0.0, 0.0, 1.0),
               half_extents=(half_width, top * 0.5, half_width), surface_id=1,
               role=ROLE_BOX)


def support_height(center, radius, obb):
    """The motor's one support-height formula, fed by the model's own contact."""
    contact = sphere_obb_contact(obb, center, radius, SKIN)
    if contact is None:
        return None, None
    normal = contact.normal
    point = contact.point
    offset = ((normal[0] * (center[0] - point[0])
               + normal[1] * (center[1] - point[1])
               + normal[2] * (center[2] - point[2])) / normal[1])
    return center[1] - offset, contact


class RestingOnABoxTopTests(unittest.TestCase):
    def test_a_resting_ball_reports_the_top_and_a_zero_gap(self):
        for top in TOPS:
            box = unit_box(top)
            for diameter in DIAMETERS:
                radius = diameter * 0.5
                center = (0.0, top + radius, 0.0)
                height, contact = support_height(center, radius, box)
                self.assertIsNotNone(contact, "the ball must touch the top")
                self.assertAlmostEqual(contact.normal[1], 1.0, delta=1e-6,
                                       msg="the support normal is the top face")
                self.assertAlmostEqual(height, top, delta=1e-6,
                                       msg="height must be the box top, not the centre")
                gap = (center[1] - radius) - height
                self.assertAlmostEqual(gap, 0.0, delta=1e-6)

    def test_the_reference_step_settles_the_ball_one_radius_above_the_top(self):
        # The state the motor must reach: drop the ball onto the box and let the
        # reference solver run; it comes to rest at top + radius, not at the top.
        for top in (0.075, 0.10):
            for diameter in DIAMETERS:
                radius = diameter * 0.5
                world = World(obbs=[unit_box(top)], ground=GROUND)
                state = State(center=(0.0, top + radius + 0.35, 0.0), radius=radius)
                for _ in range(80):
                    step(world, state, (0.0, 0.0, 1.0), 0.0, DT)
                self.assertAlmostEqual(state.center[1], top + radius, delta=0.002,
                                       msg="settled centre, D=" + str(diameter))
                height, contact = support_height(state.center, radius, world.obbs[0])
                self.assertIsNotNone(contact)
                self.assertAlmostEqual(height, top, delta=0.002)
                self.assertAlmostEqual((state.center[1] - radius) - height, 0.0,
                                       delta=0.002)

    def test_an_edge_contact_reports_a_tilted_tangent_plane_and_a_zero_gap(self):
        # Rolling over a lip: the ball's centre is outside the footprint, so the closest
        # point is the top EDGE and the support plane is tilted. Its height under the
        # centre is therefore below the edge - but the criterion only asks that the
        # BOTTOM sit on that plane, i.e. gap = -depth / n.y, which stays inside the
        # solver's slop. This is the invariant the test field's rest_gap relies on.
        top = 0.075
        box = unit_box(top, half_width=0.5)
        radius = 0.15
        center = (0.5 + 0.05, top + radius - 0.02, 0.0)
        height, contact = support_height(center, radius, box)
        self.assertIsNotNone(contact)
        self.assertLess(contact.normal[1], 0.999,
                        "an edge contact's normal is tilted, not the face normal")
        self.assertLess(height, top)
        # gap = distance / n.y - radius with distance = radius - depth: on a face that is
        # exactly -depth, and on a tilted edge plane it is the same order of magnitude.
        gap = (center[1] - radius) - height
        distance = radius - contact.depth
        expected = distance / contact.normal[1] - radius
        self.assertAlmostEqual(gap, expected, delta=1e-6)
        self.assertLess(abs(gap), 0.005)


class VerticalWallTests(unittest.TestCase):
    def test_a_vertical_contact_has_no_floor_height(self):
        # This is the pass6 signature: a 90 deg contact's closest point is at the
        # ball's own height and its normal has no +Y part, so the §2.5 formula
        # degenerates. The motor therefore must not use a climb contact's point as a
        # floor height (it reports the ball's bottom instead).
        radius = 0.15
        wall = Obb(center=(0.0, 1.5, 2.5), axis_x=(1.0, 0.0, 0.0),
                   axis_y=(0.0, 1.0, 0.0), axis_z=(0.0, 0.0, 1.0),
                   half_extents=(2.0, 1.5, 0.5), surface_id=1000,
                   role=ROLE_BLOCKING)
        center = (0.0, 0.19, 2.0 - radius)
        contact = sphere_obb_contact(wall, center, radius, SKIN)
        self.assertIsNotNone(contact)
        self.assertAlmostEqual(contact.normal[2], -1.0, delta=1e-6)
        self.assertAlmostEqual(contact.normal[1], 0.0, delta=1e-9,
                               msg="a wall has no upward component")
        self.assertAlmostEqual(contact.point[1], center[1], delta=0.001,
                               msg="the wall point's y is the ball's own height")
        # The formula divides by n.y: undefined on a wall.
        with self.assertRaises(ZeroDivisionError):
            support_height(center, radius, wall)
        # What the old climb branch did instead (height = point.y) reads exactly -r,
        # which is the value pass6 reported for C08/C09/C10.
        legacy_gap = (center[1] - radius) - contact.point[1]
        self.assertAlmostEqual(legacy_gap, -radius, delta=0.002)

    def test_the_reference_model_keeps_a_climb_alive_with_a_zero_gate(self):
        # CHARACTERISATION, not an endorsement: the reference model's step() only
        # leaves a climb at the cap or on pushIn < p_min, so releasing the forward
        # input while still facing the wall leaves the ball hanging with climb speed
        # 0. The motor ends the climb on the standing gate (design §3.2/§3.3) instead;
        # this test keeps the model's behaviour visible so the difference cannot be
        # mistaken for drift.
        radius = 0.15
        world = World(obbs=[], ground=GROUND,
                      planes=[Plane((0.0, 0.0, -1.0), -2.0)])
        state = State(center=(0.0, radius, 2.0 - radius), radius=radius)
        drive_dir = (0.0, 0.0, 1.0)
        for _ in range(40):
            step(world, state, drive_dir, 4.0, DT)
        height_before = state.center[1]
        self.assertGreater(height_before, radius + 0.02,
                           "a full push must have lifted the ball up the wall")
        # Release: the target speed is zero, the facing direction is not. The climb rate
        # ramps down (a_climb) over a few frames, then the ball hangs with climb speed 0
        # and never leaves the channel: the model has no "gate closed" exit.
        for _ in range(20):
            report = step(world, state, drive_dir, 0.0, DT)
        self.assertLess(report.climb_speed, 0.002,
                        "the climb rate decays to zero, not to a slide back")
        resting_height = state.center[1]
        for _ in range(20):
            step(world, state, drive_dir, 0.0, DT)
        self.assertAlmostEqual(state.center[1], resting_height, delta=0.005,
                               msg="the model leaves the ball hanging on the wall")
        self.assertGreater(state.center[1], height_before - 0.02,
                           "it keeps the height it climbed to")
        self.assertGreater(state.center[1], radius + 0.02,
                           "and it is still off the ground")


if __name__ == "__main__":
    unittest.main()
