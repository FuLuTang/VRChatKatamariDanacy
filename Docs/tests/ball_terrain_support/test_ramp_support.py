"""Port of Tests/EditMode/BallLayers/RampSupportTests.cs (15 cases).

The C# is the runtime authority and this file is its offline mirror; case names
map one-to-one (CamelCase -> snake_case). Lengths are metres, +Y up. Expected
values are hand calculations or geometric invariants, never a re-run of the
implementation formula.

Conventions: a ramp normal is ``(0, cos(slope), -sin(slope))``, so downhill is
+Z and the plane height falls as z grows; ``center`` is a point on the walking
plane (the output of :func:`bake_ramp`), not the box centre.
"""
import math
import unittest

from Docs.Design.BallLayers.TerrainSupport.terrain_support import (
    FIRST_PROP_SURFACE_ID,
    FIRST_RAMP_SURFACE_ID,
    GROUND_SURFACE_ID,
    MAX_RAMPS,
    NO_SURFACE_ID,
    RAMP_REFUSAL_DEGENERATE_AXIS,
    RAMP_REFUSAL_INVALID_INPUT,
    RAMP_REFUSAL_NONE,
    RAMP_REFUSAL_NORMAL_NOT_UPWARD,
    RAMP_REFUSAL_SLOPE_TOO_STEEP,
    RampRefusal,
    apply_ramp_tangential_gravity,
    bake_ramp,
    is_ramp_count_allowed,
    is_ramp_surface_id,
    is_step_crossing,
    is_step_within_limit,
    ramp_refusal_reason,
    ramp_slope_degrees,
    ramp_surface_index,
    try_resolve_support,
    try_step_height_limit,
    validate_ramp,
)

UP = (0.0, 1.0, 0.0)
STEP_RATIO = 0.25
MAX_SLOPE = 45.0
TOLERANCE = 1e-9


def step_limit(diameter):
    return try_step_height_limit(diameter, STEP_RATIO)


def ramp_normal(slope_degrees):
    radians = math.radians(slope_degrees)
    return (0.0, math.cos(radians), -math.sin(radians))


def resolve(probe, reference=0.0, limit=10.0, boxes=(), box_steppable=None,
            ramps=(), ramp_count=None, props=()):
    box_list = list(boxes)
    if box_steppable is None:
        box_steppable = [is_step_within_limit(box[1][1], reference, limit)
                         for box in box_list]
    return try_resolve_support(probe, reference, limit, UP, 0.0, box_list,
                               box_steppable, len(box_list), list(ramps),
                               ramp_count, list(props))


def resolve_one_ramp(probe, reference, limit, center, normal, right, half_width,
                     half_run):
    return resolve(probe, reference, limit,
                   ramps=[(center, normal, right, half_width, half_run)])


class RampSupportTests(unittest.TestCase):
    # ------------------------------------------------------------ 1 ----
    def test_ramp_height_matches_the_plane_equation(self):
        normal = ramp_normal(30.0)
        center = (0.0, 0.5, 0.0)
        right = (1.0, 0.0, 0.0)
        tan30 = math.tan(math.radians(30.0))

        surface = resolve_one_ramp((0.0, 2.0, 0.0), 0.0, 10.0,
                                   center, normal, right, 0.5, 4.0)
        self.assertEqual(surface.surface_id, FIRST_RAMP_SURFACE_ID)
        self.assertAlmostEqual(surface.height, 0.5, delta=TOLERANCE)
        self.assertEqual(surface.point, (0.0, 0.5, 0.0))
        for axis in range(3):
            self.assertAlmostEqual(surface.normal[axis], normal[axis], delta=TOLERANCE)

        # This normal tilts toward -Z, so the plane rises toward +Z.
        uphill = resolve_one_ramp((0.0, 2.0, 1.0), 0.0, 10.0,
                                  center, normal, right, 0.5, 4.0)
        self.assertEqual(uphill.surface_id, FIRST_RAMP_SURFACE_ID)
        self.assertAlmostEqual(uphill.height, 0.5 + tan30, delta=TOLERANCE)
        self.assertEqual(uphill.point, (0.0, uphill.height, 1.0))

        downhill = resolve_one_ramp((0.0, 2.0, -0.5), 0.0, 10.0,
                                    center, normal, right, 0.5, 4.0)
        self.assertEqual(downhill.surface_id, FIRST_RAMP_SURFACE_ID)
        self.assertAlmostEqual(downhill.height, 0.5 - tan30 * 0.5, delta=TOLERANCE)

    # ------------------------------------------------------------ 2 ----
    def test_ramp_footprint_rejects_points_outside_its_horizontal_rectangle(self):
        normal = ramp_normal(30.0)
        center = (0.0, 1.0, 0.0)
        right = (1.0, 0.0, 0.0)

        for probe in ((0.5, 2.0, 0.0), (-0.5, 2.0, 0.0),
                      (0.0, 2.0, 1.0), (0.0, 2.0, -1.0)):
            self.assertEqual(resolve_one_ramp(probe, 0.0, 10.0,
                                              center, normal, right, 0.5, 1.0).surface_id,
                             FIRST_RAMP_SURFACE_ID, probe)

        self.assertEqual(resolve_one_ramp((0.5001, 2.0, 0.0), 0.0, 10.0,
                                          center, normal, right, 0.5, 1.0).surface_id,
                         GROUND_SURFACE_ID)
        self.assertEqual(resolve_one_ramp((0.0, 2.0, 1.0001), 0.0, 10.0,
                                          center, normal, right, 0.5, 1.0).surface_id,
                         GROUND_SURFACE_ID)

        # The footprint is horizontal: height above the plane does not matter.
        self.assertEqual(resolve_one_ramp((0.25, 20.0, 0.5), 0.0, 10.0,
                                          center, normal, right, 0.5, 1.0).surface_id,
                         FIRST_RAMP_SURFACE_ID)

    # ------------------------------------------------------------ 3 ----
    def test_a_ramp_steeper_than_the_limit_is_refused_with_a_reason(self):
        steep = ramp_normal(58.0)
        self.assertEqual(validate_ramp((0.0, 0.0, 0.0), steep, (1.0, 0.0, 0.0),
                                       1.0, 1.0, MAX_SLOPE),
                         RAMP_REFUSAL_SLOPE_TOO_STEEP)
        self.assertAlmostEqual(ramp_slope_degrees(steep), 58.0, delta=1e-9)
        self.assertEqual(ramp_refusal_reason(RAMP_REFUSAL_NONE), "")
        self.assertNotEqual(ramp_refusal_reason(RAMP_REFUSAL_SLOPE_TOO_STEEP), "")

        self.assertEqual(validate_ramp((0.0, 0.0, 0.0), ramp_normal(45.1),
                                       (1.0, 0.0, 0.0), 1.0, 1.0, MAX_SLOPE),
                         RAMP_REFUSAL_SLOPE_TOO_STEEP)
        self.assertEqual(validate_ramp((0.0, 0.0, 0.0), ramp_normal(44.9),
                                       (1.0, 0.0, 0.0), 1.0, 1.0, MAX_SLOPE),
                         RAMP_REFUSAL_NONE)
        self.assertEqual(validate_ramp((0.0, 0.0, 0.0), steep, (1.0, 0.0, 0.0),
                                       1.0, 1.0, 60.0), RAMP_REFUSAL_NONE)

        self.assertTrue(is_ramp_count_allowed(MAX_RAMPS))
        self.assertFalse(is_ramp_count_allowed(MAX_RAMPS + 1))
        self.assertFalse(is_ramp_count_allowed(-1))
        with self.assertRaises(RampRefusal) as caught:
            bake_ramp((0.0, 0.0, 0.0), steep, (1.0, 0.0, 0.0),
                      (0.35, 0.04, 1.3984), MAX_SLOPE)
        self.assertEqual(caught.exception.code, RAMP_REFUSAL_SLOPE_TOO_STEEP)

    # ------------------------------------------------------------ 4 ----
    def test_a_ramp_with_a_non_upward_normal_is_refused(self):
        self.assertEqual(validate_ramp((0.0, 0.0, 0.0), (1.0, 0.0, 0.0),
                                       (0.0, 0.0, 1.0), 1.0, 1.0, MAX_SLOPE),
                         RAMP_REFUSAL_NORMAL_NOT_UPWARD)
        self.assertEqual(validate_ramp((0.0, 0.0, 0.0), (0.0, 0.05, 0.99875),
                                       (1.0, 0.0, 0.0), 1.0, 1.0, MAX_SLOPE),
                         RAMP_REFUSAL_NORMAL_NOT_UPWARD)
        self.assertEqual(validate_ramp((0.0, 0.0, 0.0), UP, (1.0, 0.0, 0.0),
                                       1.0, 1.0, MAX_SLOPE),
                         RAMP_REFUSAL_DEGENERATE_AXIS)
        self.assertEqual(validate_ramp((0.0, 0.0, 0.0), ramp_normal(30.0),
                                       UP, 1.0, 1.0, MAX_SLOPE),
                         RAMP_REFUSAL_DEGENERATE_AXIS)

    # ------------------------------------------------------------ 5 ----
    def test_bake_from_a_transformed_box_yields_the_expected_plane(self):
        size = (0.35, 0.04, 1.3984)
        box_center = (2.0, 1.0, 3.0)
        box_normal = ramp_normal(30.0)
        ramp = bake_ramp(box_center, box_normal, (1.0, 0.0, 0.0), size, MAX_SLOPE)

        self.assertAlmostEqual(ramp.center[0], box_center[0], delta=TOLERANCE)
        self.assertAlmostEqual(ramp.center[1],
                               box_center[1] + box_normal[1] * size[1] * 0.5,
                               delta=TOLERANCE)
        self.assertAlmostEqual(ramp.center[2],
                               box_center[2] + box_normal[2] * size[1] * 0.5,
                               delta=TOLERANCE)
        for axis in range(3):
            self.assertAlmostEqual(ramp.normal[axis], box_normal[axis], delta=TOLERANCE)
        self.assertAlmostEqual(ramp.right[1], 0.0, delta=TOLERANCE)
        self.assertAlmostEqual(ramp.right[0], 1.0, delta=TOLERANCE)
        self.assertAlmostEqual(ramp.half_width, 0.175, delta=TOLERANCE)
        self.assertAlmostEqual(ramp.half_run,
                               size[2] * 0.5 * math.cos(math.radians(30.0)),
                               delta=TOLERANCE)
        self.assertAlmostEqual(ramp.slope_degrees, 30.0, delta=0.01)
        self.assertNotAlmostEqual(ramp.center[1], box_center[1], delta=1e-6)

        steep_normal = ramp_normal(45.0)
        steep = bake_ramp((-1.0, 0.5, 0.25), steep_normal, (1.0, 0.0, 0.0),
                          (2.0, 0.1, 2.0), MAX_SLOPE)
        self.assertAlmostEqual(steep.half_width, 1.0, delta=TOLERANCE)
        self.assertAlmostEqual(steep.half_run,
                               1.0 * math.cos(math.radians(45.0)), delta=TOLERANCE)
        self.assertAlmostEqual(steep.slope_degrees, 45.0, delta=0.01)

    # ------------------------------------------------------------ 6 ----
    def test_half_run_uses_the_horizontal_projection(self):
        slope_degrees = 10.7
        size = (0.35, 0.04, 1.3984)
        ramp = bake_ramp((0.0, 0.0, 0.0), ramp_normal(slope_degrees),
                         (1.0, 0.0, 0.0), size, MAX_SLOPE)
        tilted_half_length = size[2] * 0.5
        expected = tilted_half_length * math.cos(math.radians(slope_degrees))
        self.assertAlmostEqual(ramp.half_run, expected, delta=TOLERANCE)
        self.assertLess(ramp.half_run, tilted_half_length)
        self.assertAlmostEqual(ramp.half_run, 0.687, delta=0.001)

    # ------------------------------------------------------------ 7 ----
    def test_capacity_refuses_the_fifth_ramp_and_keeps_the_previous_four(self):
        self.assertTrue(is_ramp_count_allowed(MAX_RAMPS))
        self.assertFalse(is_ramp_count_allowed(MAX_RAMPS + 1))

        ramps = [((0.0, 0.1 * (i + 1), 0.0), ramp_normal(30.0), (1.0, 0.0, 0.0),
                  5.0, 5.0) for i in range(5)]

        four = resolve((0.0, 2.0, 0.0), 0.0, 10.0, ramps=ramps[:4])
        self.assertEqual(four.surface_id, FIRST_RAMP_SURFACE_ID + 3)
        self.assertAlmostEqual(four.height, 0.4, delta=TOLERANCE)
        self.assertEqual(four.candidate_count, 5)

        one = resolve((0.0, 2.0, 0.0), 0.0, 10.0, ramps=ramps[:1])
        self.assertEqual(one.surface_id, FIRST_RAMP_SURFACE_ID)
        self.assertAlmostEqual(one.height, 0.1, delta=TOLERANCE)
        self.assertEqual(one.candidate_count, 2)

    # ------------------------------------------------------------ 8 ----
    def test_the_ramp_foot_meets_the_floor_without_a_step(self):
        run = 0.70 / math.tan(math.radians(30.0))
        center = (0.0, 0.35, run * 0.5)
        normal = ramp_normal(30.0)
        half_run = run * 0.5

        # The plane rises toward +Z, so the foot is at z = 0 and the top at z = run.
        at_foot = resolve_one_ramp((0.0, 2.0, 0.0), 0.0, 10.0,
                                   center, normal, (1.0, 0.0, 0.0), 0.175, half_run)
        self.assertEqual(at_foot.surface_id, FIRST_RAMP_SURFACE_ID)
        self.assertAlmostEqual(at_foot.height, 0.0, delta=0.001)

        just_inside = resolve_one_ramp((0.0, 2.0, 0.005), 0.0, 10.0,
                                       center, normal, (1.0, 0.0, 0.0), 0.175, half_run)
        self.assertEqual(just_inside.surface_id, FIRST_RAMP_SURFACE_ID)
        self.assertLess(just_inside.height, 0.005)
        self.assertFalse(is_step_crossing(just_inside.height, 0.005))

        just_outside = resolve_one_ramp((0.0, 2.0, -0.01), 0.0, 10.0,
                                        center, normal, (1.0, 0.0, 0.0), 0.175, half_run)
        self.assertEqual(just_outside.surface_id, GROUND_SURFACE_ID)
        self.assertAlmostEqual(just_outside.height, 0.0, delta=TOLERANCE)

    # ------------------------------------------------------------ 9 ----
    def test_a_ramp_registered_as_a_box_would_be_a_wall_at_its_foot(self):
        boxes = [((-0.175, 0.0, 0.0), (0.175, 0.7346, 1.2124))]
        as_box = resolve((0.0, 2.0, 0.2), 0.0, 0.275, boxes=boxes)
        self.assertEqual(as_box.surface_id, GROUND_SURFACE_ID)
        self.assertEqual(as_box.candidate_count, 1)

        run = 0.70 / math.tan(math.radians(30.0))
        as_ramp = resolve_one_ramp((0.0, 2.0, 0.2), 0.0, 0.275,
                                   (0.0, 0.35, run * 0.5), ramp_normal(30.0),
                                   (1.0, 0.0, 0.0), 0.175, run * 0.5)
        self.assertEqual(as_ramp.surface_id, FIRST_RAMP_SURFACE_ID)
        self.assertGreater(as_ramp.height, 0.0)

    # ----------------------------------------------------------- 10 ----
    def test_an_empty_ramp_table_leaves_the_pre_ramp_surface(self):
        center = (0.0, 0.5, 0.0)
        normal = ramp_normal(30.0)
        with_count_zero = resolve((0.0, 2.0, 0.0), 0.0, 0.275,
                                  ramps=[(center, normal, (1.0, 0.0, 0.0), 1.0, 1.0)],
                                  ramp_count=0)
        with_empty_table = resolve((0.0, 2.0, 0.0), 0.0, 0.275)

        self.assertEqual(with_count_zero.surface_id, with_empty_table.surface_id)
        self.assertEqual(with_count_zero.height, with_empty_table.height)
        self.assertEqual(with_count_zero.point, with_empty_table.point)
        self.assertEqual(with_count_zero.normal, with_empty_table.normal)
        self.assertEqual(with_count_zero.candidate_count,
                         with_empty_table.candidate_count)
        self.assertEqual(with_count_zero.surface_id, GROUND_SURFACE_ID)

    # ----------------------------------------------------------- 11 ----
    def test_the_highest_surface_wins_across_boxes_ramps_and_props(self):
        boxes = [((-1.0, 0.0, -1.0), (1.0, 0.3, 1.0))]
        props = [((-1.0, 0.0, -1.0), (1.0, 0.32, 1.0))]

        ramp_wins = resolve((0.0, 2.0, 0.0), 0.0, 1.0, boxes=boxes,
                            ramps=[((0.0, 0.35, 0.0), ramp_normal(30.0),
                                    (1.0, 0.0, 0.0), 5.0, 5.0)], props=props)
        self.assertEqual(ramp_wins.surface_id, FIRST_RAMP_SURFACE_ID)
        self.assertAlmostEqual(ramp_wins.height, 0.35, delta=TOLERANCE)

        prop_wins = resolve((0.0, 2.0, 0.0), 0.0, 1.0, boxes=boxes,
                            ramps=[((0.0, 0.25, 0.0), ramp_normal(30.0),
                                    (1.0, 0.0, 0.0), 5.0, 5.0)], props=props)
        self.assertEqual(prop_wins.surface_id, FIRST_PROP_SURFACE_ID)
        self.assertAlmostEqual(prop_wins.height, 0.32, delta=TOLERANCE)

        box_tie = resolve((0.0, 2.0, 0.0), 0.0, 1.0,
                          boxes=[((-1.0, 0.0, -1.0), (1.0, 0.32, 1.0))],
                          ramps=[((0.0, 0.32, 0.0), ramp_normal(30.0),
                                  (1.0, 0.0, 0.0), 5.0, 5.0)], props=props)
        self.assertEqual(box_tie.surface_id, 1)

        ramp_tie = resolve((0.0, 2.0, 0.0), 0.0, 1.0, boxes=boxes,
                           ramps=[((0.0, 0.32, 0.0), ramp_normal(30.0),
                                   (1.0, 0.0, 0.0), 5.0, 5.0)], props=props)
        self.assertEqual(ramp_tie.surface_id, FIRST_RAMP_SURFACE_ID)
        self.assertAlmostEqual(ramp_tie.height, 0.32, delta=TOLERANCE)

        outside = resolve((0.0, 2.0, 0.0), 0.0, 1.0, boxes=boxes,
                          ramps=[((10.0, 0.35, 0.0), ramp_normal(30.0),
                                  (1.0, 0.0, 0.0), 5.0, 5.0)], props=props)
        self.assertEqual(outside.surface_id, FIRST_PROP_SURFACE_ID)
        self.assertAlmostEqual(outside.height, 0.32, delta=TOLERANCE)

    # ----------------------------------------------------------- 12 ----
    def test_ramp_surface_ids_never_collide_with_box_or_prop_ids(self):
        self.assertTrue(is_ramp_surface_id(FIRST_RAMP_SURFACE_ID))
        self.assertTrue(is_ramp_surface_id(FIRST_RAMP_SURFACE_ID + MAX_RAMPS - 1))
        self.assertFalse(is_ramp_surface_id(FIRST_RAMP_SURFACE_ID - 1))
        self.assertFalse(is_ramp_surface_id(FIRST_RAMP_SURFACE_ID + MAX_RAMPS))
        self.assertFalse(is_ramp_surface_id(GROUND_SURFACE_ID))
        self.assertFalse(is_ramp_surface_id(1))
        self.assertFalse(is_ramp_surface_id(FIRST_PROP_SURFACE_ID))
        self.assertEqual(ramp_surface_index(FIRST_RAMP_SURFACE_ID), 0)
        self.assertEqual(ramp_surface_index(FIRST_RAMP_SURFACE_ID + 3), 3)
        self.assertEqual(ramp_surface_index(1), -1)

    # ----------------------------------------------------------- 13 ----
    def test_non_finite_ramp_input_is_refused_and_nothing_is_written(self):
        with self.assertRaises(ValueError):
            validate_ramp((float("nan"), 0.0, 0.0), ramp_normal(30.0),
                          (1.0, 0.0, 0.0), 1.0, 1.0, MAX_SLOPE)
        with self.assertRaises(ValueError):
            bake_ramp((0.0, 0.0, 0.0), ramp_normal(30.0), (1.0, 0.0, 0.0),
                      (0.35, float("nan"), 1.4), MAX_SLOPE)
        with self.assertRaises(RampRefusal) as caught:
            bake_ramp((0.0, 0.0, 0.0), ramp_normal(30.0), (1.0, 0.0, 0.0),
                      (-0.35, 0.04, 1.4), MAX_SLOPE)
        self.assertEqual(caught.exception.code, RAMP_REFUSAL_INVALID_INPUT)

        valid = [((-1.0, 0.0, -1.0), (1.0, 0.3, 1.0))]
        with self.assertRaises(ValueError):
            resolve((0.0, 2.0, 0.0), 0.0, 1.0, boxes=valid,
                    ramps=[((float("nan"), 0.0, 0.0), ramp_normal(30.0),
                            (1.0, 0.0, 0.0), 1.0, 1.0)])
        with self.assertRaises(ValueError):
            resolve((0.0, 2.0, 0.0), 0.0, 1.0, boxes=valid,
                    ramps=[((0.0, 0.5, 0.0), ramp_normal(30.0),
                            (1.0, 0.0, 0.0), 0.0, 1.0)])
        # A mask shorter than box_count is refused rather than read past the end.
        with self.assertRaises(ValueError):
            try_resolve_support((0.0, 2.0, 0.0), 0.0, 1.0, UP, 0.0,
                                [(b[0], b[1]) for b in valid], [], 1)

    # ----------------------------------------------------------- 14 ----
    def test_the_ramp_top_is_flush_with_the_table_edge(self):
        run = 0.70 / math.tan(math.radians(30.0))
        boxes = [((-1.0, 0.0, run), (1.0, 0.70, run + 1.0))]
        ramps = [((0.0, 0.35, run * 0.5), ramp_normal(30.0), (1.0, 0.0, 0.0),
                  0.175, run * 0.5)]

        at_seam = resolve((0.0, 2.0, run), 0.0, 1.0, boxes=boxes, ramps=ramps)
        self.assertEqual(at_seam.surface_id, 1)
        self.assertAlmostEqual(at_seam.height, 0.70, delta=TOLERANCE)

        just_on_ramp = resolve((0.0, 2.0, run - 0.001), 0.0, 1.0, boxes=boxes,
                               ramps=ramps)
        self.assertEqual(just_on_ramp.surface_id, FIRST_RAMP_SURFACE_ID)
        self.assertAlmostEqual(just_on_ramp.height, 0.70, delta=0.001)
        self.assertAlmostEqual(just_on_ramp.height, at_seam.height, delta=0.001)

        just_on_table = resolve((0.0, 2.0, run + 0.001), 0.0, 1.0, boxes=boxes,
                                ramps=ramps)
        self.assertEqual(just_on_table.surface_id, 1)
        self.assertAlmostEqual(just_on_table.height, 0.70, delta=TOLERANCE)

    # ----------------------------------------------------------- 15 ----
    def test_the_tangential_gravity_is_off_by_default_and_pushes_downhill(self):
        velocity = (0.3, 0.0, 0.1)

        unchanged = apply_ramp_tangential_gravity(velocity, ramp_normal(30.0),
                                                  9.81, 0.0, 0.02, 4.0)
        self.assertEqual(unchanged, velocity)

        next_velocity = apply_ramp_tangential_gravity((0.0, 0.0, 0.0),
                                                      ramp_normal(30.0),
                                                      9.81, 1.0, 0.02, 4.0)
        self.assertAlmostEqual(next_velocity[0], 0.0, delta=TOLERANCE)
        self.assertAlmostEqual(next_velocity[1], 0.0, delta=TOLERANCE)
        self.assertAlmostEqual(next_velocity[2], -9.81 * 0.5 * 0.02, delta=TOLERANCE)

        flat = apply_ramp_tangential_gravity(velocity, UP, 9.81, 1.0, 0.02, 4.0)
        self.assertEqual(flat, velocity)

        capped = apply_ramp_tangential_gravity((0.0, 0.0, -3.99), ramp_normal(30.0),
                                               9.81, 1.0, 0.02, 4.0)
        self.assertAlmostEqual(capped[2], -4.0, delta=TOLERANCE)
        self.assertAlmostEqual(capped[0], 0.0, delta=TOLERANCE)

        with self.assertRaises(ValueError):
            apply_ramp_tangential_gravity(velocity, ramp_normal(30.0),
                                          9.81, 1.5, 0.02, 4.0)
        with self.assertRaises(ValueError):
            apply_ramp_tangential_gravity(velocity, ramp_normal(30.0),
                                          9.81, 1.0, 0.0, 4.0)

        self.assertAlmostEqual(ramp_slope_degrees(UP), 0.0, delta=TOLERANCE)
        self.assertAlmostEqual(ramp_slope_degrees(ramp_normal(30.0)), 30.0, delta=0.01)


if __name__ == "__main__":
    unittest.main()
