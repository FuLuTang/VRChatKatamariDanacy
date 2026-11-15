"""Port of Tests/EditMode/BallLayers/SupportSurfaceTests.cs (17 cases).

The C# is the runtime authority and this file is its offline mirror; case names
map one-to-one (CamelCase -> snake_case).  Lengths are metres, +Y up.  Expected
values are hand calculations or geometric invariants, never a re-run of the
implementation formula.
"""
import math
import unittest

from Docs.Design.BallLayers.TerrainSupport.terrain_support import (
    GROUND_SURFACE_ID,
    NO_SURFACE_ID,
    is_step_crossing,
    is_steppable,
    resolve_fall_state,
    support_dropped_away,
    try_integrate_fall,
    try_resolve_support,
    try_step_height_limit,
)

UP = (0.0, 1.0, 0.0)
STEP_RATIO = 0.25
TOLERANCE = 1e-9

# The assembly layer cannot be built here, so the motor's mask/select/lift caps
# are exercised through their pure functions.  The selection helper mirrors the
# motor's call shape: ground offset 0, upward normal.
def resolve(probe, reference=0.0, limit=0.3, boxes=(), box_count=None):
    return try_resolve_support(probe, reference, limit, UP, 0.0, list(boxes), box_count)


def box(minimum, maximum):
    return (minimum, maximum)


def step_limit(diameter):
    return try_step_height_limit(diameter, STEP_RATIO)


class SupportSurfaceTests(unittest.TestCase):
    # ------------------------------------------------------------ 1 ----
    def test_ground_only_returns_the_ground_plane(self):
        surface = try_resolve_support((1.0, 5.0, 2.0), 0.0, 0.6, UP, 0.5, [])
        self.assertEqual(surface.surface_id, GROUND_SURFACE_ID)
        self.assertAlmostEqual(surface.height, 0.5, delta=TOLERANCE)
        self.assertEqual(surface.point, (1.0, 0.5, 2.0))
        self.assertEqual(surface.normal, UP)
        self.assertEqual(surface.candidate_count, 1)

    # ------------------------------------------------------------ 2 ----
    def test_box_top_wins_inside_its_footprint(self):
        surface = resolve((0.0, 1.0, 0.0), limit=0.275,
                          boxes=[box((-1.0, 0.0, -1.0), (1.0, 0.26, 1.0))])
        self.assertEqual(surface.surface_id, 1)
        self.assertAlmostEqual(surface.height, 0.26, delta=TOLERANCE)
        self.assertEqual(surface.point, (0.0, 0.26, 0.0))
        self.assertEqual(surface.normal, UP)
        self.assertEqual(surface.candidate_count, 2)

    # ------------------------------------------------------------ 3 ----
    def test_probe_on_the_footprint_edge_counts_as_inside(self):
        boxes = [box((0.0, 0.0, 0.0), (1.0, 0.2, 1.0))]
        self.assertEqual(resolve((1.0, 1.0, 0.0), limit=0.275, boxes=boxes).surface_id, 1)
        self.assertEqual(resolve((0.0, 1.0, 1.0), limit=0.275, boxes=boxes).surface_id, 1)

    # ------------------------------------------------------------ 4 ----
    def test_probe_outside_the_footprint_falls_back_to_ground(self):
        boxes = [box((0.0, 0.0, 0.0), (1.0, 0.2, 1.0))]
        surface = resolve((1.0001, 1.0, 0.0), limit=0.275, boxes=boxes)
        self.assertEqual(surface.surface_id, GROUND_SURFACE_ID)
        self.assertAlmostEqual(surface.height, 0.0, delta=TOLERANCE)
        self.assertEqual(surface.candidate_count, 2)

    # ------------------------------------------------------------ 5 ----
    def test_highest_surface_wins_when_two_footprints_overlap(self):
        boxes = [box((-2.0, 0.0, -2.0), (2.0, 0.2, 2.0)),
                 box((-1.0, 0.0, -1.0), (1.0, 0.3, 1.0))]
        surface = resolve((0.0, 1.0, 0.0), limit=0.35, boxes=boxes)
        self.assertEqual(surface.surface_id, 2)
        self.assertAlmostEqual(surface.height, 0.3, delta=TOLERANCE)
        self.assertEqual(surface.candidate_count, 3)

    # ------------------------------------------------------------ 6 ----
    def test_surfaces_above_the_step_band_are_not_candidates(self):
        wall = [box((-1.0, 0.0, -1.0), (1.0, 1.0, 1.0))]
        surface = resolve((0.0, 0.5, 0.0), limit=step_limit(1.10), boxes=wall)
        self.assertEqual(surface.surface_id, GROUND_SURFACE_ID)
        self.assertAlmostEqual(surface.height, 0.0, delta=TOLERANCE)
        self.assertEqual(surface.candidate_count, 1)

        # The band edge is inclusive: a top exactly at reference + limit counts.
        edge = [box((-1.0, 0.0, -1.0), (1.0, step_limit(1.10), 1.0))]
        surface = resolve((0.0, 0.5, 0.0), limit=step_limit(1.10), boxes=edge)
        self.assertEqual(surface.surface_id, 1)
        self.assertAlmostEqual(surface.height, step_limit(1.10), delta=TOLERANCE)

    # ------------------------------------------------------------ 7 ----
    def test_step_limit_scales_with_diameter(self):
        self.assertAlmostEqual(try_step_height_limit(0.30, 0.25), 0.075, delta=TOLERANCE)
        self.assertAlmostEqual(try_step_height_limit(0.83, 0.25), 0.2075, delta=TOLERANCE)
        self.assertAlmostEqual(try_step_height_limit(1.10, 0.25), 0.275, delta=TOLERANCE)
        self.assertAlmostEqual(try_step_height_limit(2.20, 0.25), 0.55, delta=TOLERANCE)
        with self.assertRaises(ValueError):
            try_step_height_limit(float("nan"), 0.25)
        with self.assertRaises(ValueError):
            try_step_height_limit(1.10, -0.1)
        with self.assertRaises(ValueError):
            try_step_height_limit(1.10, 1.5)

    # ------------------------------------------------------------ 8 ----
    def test_the_box_that_is_within_the_limit_leaves_the_sweep(self):
        self.assertTrue(is_steppable(0.26, step_limit(1.10), False, False, 0.02))
        self.assertFalse(is_steppable(0.26, step_limit(0.30), False, False, 0.02))
        self.assertTrue(is_steppable(-0.1, 0.0, False, False, 0.0))
        with self.assertRaises(ValueError):
            is_steppable(float("nan"), 0.275, False, False, 0.02)
        with self.assertRaises(ValueError):
            is_steppable(0.1, -1.0, False, False, 0.02)

    # ------------------------------------------------------------ 9 ----
    def test_hysteresis_keeps_a_steppable_box_out_until_it_exceeds_the_limit_plus_the_margin(
            self):
        self.assertTrue(is_steppable(0.28, 0.275, False, True, 0.02))
        self.assertFalse(is_steppable(0.28, 0.275, False, False, 0.02))
        self.assertFalse(is_steppable(0.30, 0.275, False, True, 0.02))
        self.assertTrue(is_steppable(0.29, 0.275, False, True, 0.02))

    # ----------------------------------------------------------- 10 ----
    def test_a_ball_already_above_the_top_is_never_pushed_out(self):
        self.assertTrue(is_steppable(0.0, 0.275, True, True, 0.02))
        self.assertTrue(is_steppable(1.0, 0.275, True, False, 0.02))
        with self.assertRaises(ValueError):
            is_steppable(float("nan"), 0.275, True, False, 0.0)

    # ----------------------------------------------------------- 11 ----
    def test_non_finite_input_is_refused_and_nothing_is_written(self):
        valid = [box((0.0, 0.0, 0.0), (1.0, 0.2, 1.0))]
        with self.assertRaises(ValueError):
            try_resolve_support((float("nan"), 1.0, 0.0), 0.0, 0.3, UP, 0.0, valid)
        with self.assertRaises(ValueError):
            try_resolve_support((0.0, 1.0, 0.0), 0.0, 0.3, UP, 0.0,
                                [box((0.0, 0.0, 0.0), (-1.0, 0.2, 1.0))])
        with self.assertRaises(ValueError):
            try_resolve_support((0.0, 1.0, 0.0), 0.0, 0.3, (1.0, 0.0, 0.0), 0.0, valid)
        with self.assertRaises(ValueError):
            try_resolve_support((0.0, 1.0, 0.0), 0.0, 0.3, UP, 0.0, valid, box_count=2)

    # ----------------------------------------------------------- 12 ----
    def test_surface_selection_is_stable_across_two_identical_calls(self):
        boxes = [box((-2.0, 0.0, -2.0), (2.0, 0.2, 2.0)),
                 box((-1.0, 0.0, -1.0), (1.0, 0.2, 1.0))]
        first = resolve((0.25, 1.0, -0.75), limit=0.4, boxes=boxes)
        second = resolve((0.25, 1.0, -0.75), limit=0.4, boxes=boxes)
        self.assertEqual(first, second)
        self.assertEqual(first.surface_id, 1, "tie keeps the first registration")

    # ----------------------------------------------------------- 13 ----
    def test_capacity_refuses_the_ninth_box_without_touching_the_first(self):
        boxes = [box((-5.0, 0.0, -5.0), (5.0, 0.1 + 0.01 * i, 5.0)) for i in range(8)]
        boxes.append(box((-5.0, 0.0, -5.0), (5.0, 5.0, 5.0)))
        surface = resolve((0.0, 1.0, 0.0), limit=10.0, boxes=boxes, box_count=8)
        self.assertEqual(surface.surface_id, 8, "the ninth slot is not read")
        self.assertAlmostEqual(surface.height, 0.17, delta=TOLERANCE)
        self.assertEqual(surface.candidate_count, 9)

        first = resolve((4.0, 1.0, 4.0), limit=10.0, boxes=boxes, box_count=1)
        self.assertEqual(first.surface_id, 1)
        self.assertAlmostEqual(first.height, 0.1, delta=TOLERANCE)

    # ----------------------------------------------------------- 14 ----
    def test_no_surface_is_ever_reported_without_the_ground(self):
        surface = resolve((0.0, 1.0, 0.0), reference=-10.0, limit=0.0, boxes=[])
        self.assertEqual(surface.surface_id, GROUND_SURFACE_ID)
        self.assertAlmostEqual(surface.height, 0.0, delta=TOLERANCE)
        self.assertEqual(surface.candidate_count, 0)

        below = [box((-1.0, -2.0, -1.0), (1.0, -0.5, 1.0))]
        surface = resolve((0.0, 1.0, 0.0), limit=1.0, boxes=below)
        self.assertEqual(surface.surface_id, GROUND_SURFACE_ID)
        self.assertAlmostEqual(surface.height, 0.0, delta=TOLERANCE)

        surface = try_resolve_support((0.0, 1.0, 0.0), 0.0, 0.3, UP, -0.05, [])
        self.assertAlmostEqual(surface.height, -0.05, delta=TOLERANCE)

    # ----------------------------------------------------------- 15 ----
    def test_walking_off_the_ledge_starts_a_fall(self):
        self.assertTrue(support_dropped_away(0.0, 0.26, 0.001))
        self.assertFalse(support_dropped_away(0.26, 0.26, 0.001))
        self.assertFalse(support_dropped_away(0.2595, 0.26, 0.001))

        self.assertTrue(resolve_fall_state(False, True, False, False))
        self.assertFalse(resolve_fall_state(False, False, True, False))
        self.assertTrue(resolve_fall_state(True, False, True, True))
        self.assertFalse(resolve_fall_state(True, False, True, False))
        self.assertTrue(resolve_fall_state(True, False, False, False))

    # ----------------------------------------------------------- 16 ----
    def test_the_fall_follows_gravity_and_lands_on_the_lower_surface(self):
        fixed_step = 0.02
        gravity = 9.81
        max_fall_speed = 40.0

        ball_bottom = 0.26
        fall_speed = 0.0
        falling = resolve_fall_state(False, True, False, False)
        steps = 0
        while falling and steps < 1000:
            fall_speed, budget = try_integrate_fall(fall_speed, gravity, fixed_step,
                                                    max_fall_speed)
            requested = -ball_bottom
            correction = max(-budget, min(budget, requested))
            drop_clamped = requested < -budget
            ball_bottom += correction
            falling = resolve_fall_state(falling, False, True, drop_clamped)
            steps += 1
        self.assertFalse(falling)
        self.assertAlmostEqual(ball_bottom, 0.0, delta=TOLERANCE)
        # Semi-implicit Euler, hand-derived: drop after k steps is
        # g*dt^2*k(k+1)/2, so 0.26 m survives 11 steps (0.258984 m) and lands on
        # the twelfth (budget 0.047088 m > remaining 0.001016 m).
        self.assertEqual(steps, 12)
        self.assertFalse(resolve_fall_state(False, False, True, False))

    # ----------------------------------------------------------- 17 ----
    def test_the_fall_is_not_limited_by_the_drop_per_step_clamp(self):
        fixed_step = 0.02
        gravity = 9.81
        max_fall_speed = 40.0
        max_drop_per_step = 0.1

        ball_bottom = 20.0
        fall_speed = 0.0
        fastest_step = 0.0
        for _ in range(75):
            fall_speed, budget = try_integrate_fall(fall_speed, gravity, fixed_step,
                                                    max_fall_speed)
            fastest_step = max(fastest_step, budget)
            ball_bottom -= budget

        self.assertGreater(fastest_step, max_drop_per_step)
        self.assertGreater(20.0 - ball_bottom, 75 * max_drop_per_step)
        self.assertAlmostEqual(fall_speed, gravity * 75 * fixed_step, delta=1e-6)
        self.assertLess(fall_speed, max_fall_speed)

    # Extra guard for the pure step-crossing predicate (the motor's lift cap).
    def test_step_crossing_ignores_numerical_noise(self):
        self.assertTrue(is_step_crossing(0.26, 0.005))
        self.assertFalse(is_step_crossing(0.004, 0.005))
        with self.assertRaises(ValueError):
            is_step_crossing(float("nan"), 0.005)


if __name__ == "__main__":
    unittest.main()
