"""Port of Tests/EditMode/BallLayers/SupportSurfaceTests.cs (20 cases).

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
    is_step_within_limit,
    resolve_fall_state,
    support_dropped_away,
    support_reference_height,
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
        self.assertTrue(is_step_within_limit(0.26, 0.0, step_limit(1.10)))
        self.assertFalse(is_step_within_limit(0.26, 0.0, step_limit(0.30)))
        self.assertTrue(is_step_within_limit(-0.1, 0.0, 0.0))
        with self.assertRaises(ValueError):
            is_step_within_limit(float("nan"), 0.0, 0.275)
        with self.assertRaises(ValueError):
            is_step_within_limit(0.1, 0.0, -1.0)
        with self.assertRaises(ValueError):
            is_step_within_limit(0.1, float("nan"), 0.275)

    # ------------------------------------------------------------ 9 ----
    def test_the_reference_height_is_the_only_memory_the_step_rule_needs(self):
        # No latch: what used to be "one-way hysteresis" is the shared reference.
        # Airborne above the floor the ball's own bottom (0.02 m) is the reference, so a
        # 0.075 m deck is inside a 0.074934 m limit; grounded, the same deck is a wall.
        limit = step_limit(0.2997369)
        self.assertTrue(is_step_within_limit(0.075, 0.02, limit))
        self.assertFalse(is_step_within_limit(0.075, 0.0, limit))
        self.assertAlmostEqual(support_reference_height(0.0, 0.02), 0.02, delta=TOLERANCE)
        self.assertAlmostEqual(support_reference_height(0.3, 0.02), 0.3, delta=TOLERANCE)
        self.assertAlmostEqual(support_reference_height(0.3, float("nan")), 0.3,
                               delta=TOLERANCE)

    # ----------------------------------------------------------- 10 ----
    def test_a_ball_already_above_the_top_is_never_pushed_out(self):
        # Standing on the box or above it: the reference is the ball's own bottom, so the
        # rise is zero or negative. This is the old unconditional "already above" rule,
        # now a consequence of the shared reference instead of a second comparison.
        limit = step_limit(0.30)
        self.assertTrue(is_step_within_limit(0.075, 0.075, limit))
        self.assertTrue(is_step_within_limit(0.075, 1.0, limit))
        with self.assertRaises(ValueError):
            is_step_within_limit(float("nan"), 0.075, limit)

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

    # ----------------------------------------------------------- 19 ----
    def test_the_quantized_diameter_puts_the_boundary_deck_above_the_limit(self):
        # The lab cannot set D = 0.300: the 0.30 preset quantizes to 0.2997369 m (one
        # volume unit = 0.0041 m^3). Its limit is 0.074934 m, so C09's 0.075 m deck is
        # ABOVE the limit: the mask must keep it in the sweep (a wall) and the support
        # table must not select it. Before the fix the mask had already dropped it, and
        # the 0.30 m ball drove through the deck.
        limit = step_limit(0.2997369)
        self.assertLess(limit, 0.075)
        self.assertGreater(0.075 - limit, 0.0)

        deck = [box((12.0, 0.0, -6.0), (18.0, 0.075, -2.0))]
        probe = (15.0, 0.15, -4.0)

        self.assertFalse(is_step_within_limit(0.075, 0.0, limit),
                         "the mask must KEEP the deck in the sweep")
        surface = resolve(probe, limit=limit, boxes=deck)
        self.assertEqual(surface.surface_id, GROUND_SURFACE_ID,
                         "a box the mask keeps as a wall must not be a support either")
        self.assertAlmostEqual(surface.height, 0.0, delta=TOLERANCE)

        # Just under the limit: it is a step, and the same call makes it a support.
        lower_top = 0.0749
        self.assertTrue(is_step_within_limit(lower_top, 0.0, limit))
        surface = resolve(probe, limit=limit, boxes=[box((12.0, 0.0, -6.0),
                                                         (18.0, lower_top, -2.0))])
        self.assertEqual(surface.surface_id, 1)
        self.assertAlmostEqual(surface.height, lower_top, delta=TOLERANCE)

    # ----------------------------------------------------------- 20 ----
    def test_the_sweep_mask_and_the_support_table_never_disagree(self):
        # The invariant the ghost step broke: for every box top and every reference the
        # mask can compute, "the mask drops it" and "the support table selects it" are the
        # same answer. The references include the mask refresh gate's +/-5 mm wobble, so a
        # jitter cannot split the two sides.
        limit = step_limit(0.2997369)
        tops = (0.0700, 0.0749, 0.0750, 0.0800, 0.0950)
        references = (0.0, 0.004, -0.004, 0.02)
        probe = (15.0, 0.15, -4.0)
        for top in tops:
            for reference in references:
                mask_step = is_step_within_limit(top, reference, limit)
                surface = resolve(probe, reference=reference, limit=limit,
                                  boxes=[box((12.0, 0.0, -6.0), (18.0, top, -2.0))])
                supported = surface.surface_id == 1
                self.assertEqual(mask_step, supported,
                                 "top=%s reference=%s" % (top, reference))

    # Extra guard for the pure step-crossing predicate (the motor's lift cap).
    def test_step_crossing_ignores_numerical_noise(self):
        self.assertTrue(is_step_crossing(0.26, 0.005))
        self.assertFalse(is_step_crossing(0.004, 0.005))
        with self.assertRaises(ValueError):
            is_step_crossing(float("nan"), 0.005)


if __name__ == "__main__":
    unittest.main()
