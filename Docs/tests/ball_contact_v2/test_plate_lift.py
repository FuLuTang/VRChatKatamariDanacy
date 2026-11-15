"""A ball rolling over a thin plate must not be lifted above the plate (C14).

pass8 (2026-11-15) left one check red: C14's ``lift_bounded``
(``lift_over_nominal_m = max(bottom - nominal)``) read +0.0189/+0.0721/+0.1346/
+0.2133 for D = 0.30/0.60/1.10/2.20, i.e. 0.06-0.12 x D.  The trajectory shows
where it comes from: the ball crosses the 0.028 m plate with ``sup=0.028`` (its
bottom is on the plate) and then, at the far end of the cell, it reaches the
arena's boundary PLANE at full push and climbs it for the last ~0.2-0.3 s of the
drive (`y = 0.19 / 0.70 / 1.32`, ``sup`` = 0.045 / 0.153 / 0.217).  The climb rate
is proportional to H_cap, i.e. to D, which is exactly why the number scales with
the diameter instead of with the plate.

This file pins both halves against the reference model:

1. over the plate, a ball of any shipped diameter keeps its bottom at most at the
   plate top (the theoretical bound: rolling over a step of height h can raise
   the bottom to h and no further), so a spatially gated lift stays <= the 1 cm
   tolerance - the physics is right;
2. the same drive continued into a boundary plane lifts the ball by the climb
   rate, i.e. ~D-proportionally - which is intended v2 behaviour (design §3.2)
   and is what the un-gated metric is actually measuring.

Run with ``python3 -m unittest discover -s Docs/tests`` from the repo root.
"""
import unittest

from Docs.Design.BallMotor.contact_v2.contact_v2 import (
    Obb,
    Plane,
    ROLE_BOX,
    State,
    World,
    step,
)

DT = 0.02
PLATE_TOP = 0.028
SPEED = 4.0
DIAMETERS = (0.30, 0.60, 1.10, 2.20)
GROUND = Plane((0.0, 1.0, 0.0), 0.0)


def plate():
    """C14's plate: 0.028 m high, long and wide (the cell's thin plate)."""
    return Obb(center=(0.0, PLATE_TOP * 0.5, 0.0), axis_x=(1.0, 0.0, 0.0),
               axis_y=(0.0, 1.0, 0.0), axis_z=(0.0, 0.0, 1.0),
               half_extents=(2.0, PLATE_TOP * 0.5, 1.0), surface_id=1,
               role=ROLE_BOX)


def run(diameter, plane_wall):
    """Drive north from z = -3; return (max lift over the plate, max lift overall)."""
    radius = diameter * 0.5
    world = World(obbs=[plate()], ground=GROUND)
    if plane_wall:
        # A boundary plane at z = +6, the same role the arena's north wall plays.
        world.planes.append(Plane((0.0, 0.0, -1.0), -6.0))
    state = State(center=(0.0, radius, -3.0), radius=radius,
                  v_planar=(0.0, 0.0, SPEED))
    lift_over_plate = -1e9
    lift_overall = -1e9
    for _ in range(450):
        step(world, state, (0.0, 0.0, 1.0), SPEED, DT)
        bottom = state.center[1] - radius
        lift = bottom - PLATE_TOP
        if lift > lift_overall:
            lift_overall = lift
        # The gate a correct criterion would use: only while the ball is over the
        # plate (the footprint plus one radius, so the entry and exit edges count).
        if abs(state.center[2]) <= 1.0 + radius:
            if lift > lift_over_plate:
                lift_over_plate = lift
    return lift_over_plate, lift_overall


class PlateCrossingTests(unittest.TestCase):
    def test_the_bottom_never_rises_above_the_plate_over_the_plate(self):
        # Theoretical bound: crossing a step of height h cannot lift the bottom above
        # h, whatever the diameter, so a gated lift must stay inside 1 cm.
        for diameter in DIAMETERS:
            over_plate, _ = run(diameter, False)
            self.assertLessEqual(over_plate, 0.01,
                                 "gated lift over the plate, D=" + str(diameter))

    def test_the_ball_actually_crosses_and_comes_back_down(self):
        # Negative control for the gate: the ball must reach the far side and rest on
        # the ground again, otherwise "lift <= 0.01" would be trivially true.
        radius = 1.10 * 0.5
        world = World(obbs=[plate()], ground=GROUND)
        state = State(center=(0.0, radius, -3.0), radius=radius,
                      v_planar=(0.0, 0.0, SPEED))
        for _ in range(450):
            step(world, state, (0.0, 0.0, 1.0), SPEED, DT)
        self.assertGreater(state.center[2], 1.0 + radius, "the ball passed the plate")
        self.assertAlmostEqual(state.center[1], radius, delta=0.005,
                               msg="and is back on the ground")


class BoundaryClimbTests(unittest.TestCase):
    def test_the_ungated_max_lift_is_the_wall_climb_and_scales_with_the_diameter(self):
        # CHARACTERISATION of the pass8 numbers: the un-gated metric sees the ball
        # climbing the boundary plane, and the climb rate is v_up0 = 1.718 H_cap /
        # T_climb with H_cap = 2r, so the lift is ~D-proportional. That is intended
        # v2 behaviour (design §3.2), not a plate artifact.
        small, small_overall = run(0.30, True)
        big, big_overall = run(1.10, True)
        self.assertLessEqual(small, 0.01, "the plate part of the run stays bounded")
        self.assertLessEqual(big, 0.01, "the plate part of the run stays bounded")
        self.assertGreater(small_overall, 0.005,
                           "the un-gated maximum includes the wall climb")
        self.assertGreater(big_overall, 0.05,
                           "a bigger ball climbs faster, so its lift is larger")
        self.assertGreater(big_overall, 3.0 * small_overall,
                           "the climb lift scales roughly with the diameter")


if __name__ == "__main__":
    unittest.main()
