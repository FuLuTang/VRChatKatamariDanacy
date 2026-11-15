"""Port of Tests/EditMode/BallLayers/PropSupportTests.cs (15 cases, P2).

The C# is the runtime authority and this file is its offline mirror; case names
map one-to-one (CamelCase -> snake_case).  Lengths are metres, +Y up.  Expected
values are hand calculations or geometric invariants, never a re-run of the
implementation formula.
"""
import unittest

from Docs.Design.BallLayers.TerrainSupport.terrain_support import (
    FIRST_PROP_SURFACE_ID,
    GROUND_SURFACE_ID,
    MAX_SUPPORT_PROPS,
    PROP_ROLE_BLOCKING,
    PROP_ROLE_NONE,
    PROP_ROLE_SUPPORT,
    classify_prop,
    is_prop_surface_id,
    is_support_prop_count_allowed,
    prop_surface_index,
    resolve_fall_state,
    support_dropped_away,
    try_integrate_fall,
    try_resolve_support,
    is_step_within_limit,
    try_step_height_limit,
)

UP = (0.0, 1.0, 0.0)
STEP_RATIO = 0.25
TOLERANCE = 1e-9


def step_limit(diameter):
    return try_step_height_limit(diameter, STEP_RATIO)


def box(minimum, maximum):
    return (minimum, maximum)


def resolve(probe, reference, limit, boxes=(), props=(), prop_count=None):
    box_list = list(boxes)
    box_steppable = [is_step_within_limit(box_list[i][1][1], reference, limit)
                     for i in range(len(box_list))]
    return try_resolve_support(probe, reference, limit, UP, 0.0, box_list,
                               box_steppable, len(box_list), [], None,
                               list(props), prop_count)


def role(beyond, top, support_height, limit, already_above):
    return classify_prop(beyond, top, support_height, limit, already_above)


class PropSupportTests(unittest.TestCase):
    # ------------------------------------------------------------ 1 ----
    def test_an_absorb_proof_low_prop_is_a_support_not_a_wall(self):
        self.assertEqual(role(True, 0.028, 0.0, step_limit(0.30), False),
                         PROP_ROLE_SUPPORT)

    # ------------------------------------------------------------ 2 ----
    def test_an_absorb_proof_prop_exactly_at_the_step_limit_is_a_support(self):
        limit = step_limit(1.0)
        self.assertAlmostEqual(limit, 0.25, delta=TOLERANCE)
        self.assertEqual(role(True, 0.5, 0.25, limit, False), PROP_ROLE_SUPPORT)

    # ------------------------------------------------------------ 3 ----
    def test_one_millimetre_above_the_limit_is_a_wall(self):
        limit = step_limit(0.30)
        self.assertEqual(role(True, limit + 0.001, 0.0, limit, False),
                         PROP_ROLE_BLOCKING)

    # ------------------------------------------------------------ 4 ----
    def test_the_same_prop_flips_to_support_when_the_ball_grows(self):
        # Absorb-proof at both sizes: the real 0.30 m cooler becomes food at D ~= 1.03,
        # so this pins the geometric branch only (design rule 9 window).
        self.assertEqual(role(True, 0.30, 0.0, step_limit(0.30), False),
                         PROP_ROLE_BLOCKING)
        self.assertEqual(role(True, 0.30, 0.0, step_limit(1.20), False),
                         PROP_ROLE_SUPPORT)

    # ------------------------------------------------------------ 5 ----
    def test_an_absorbable_prop_is_neither_a_wall_nor_a_support(self):
        self.assertEqual(role(False, 0.028, 0.0, step_limit(0.30), False),
                         PROP_ROLE_NONE)
        self.assertEqual(role(False, 1.8, 0.0, step_limit(0.30), False),
                         PROP_ROLE_NONE)

    # ------------------------------------------------------------ 6 ----
    def test_a_ball_already_above_the_top_never_gets_a_wall(self):
        self.assertEqual(role(True, 1.0, 0.0, step_limit(0.30), True),
                         PROP_ROLE_SUPPORT)

    # ------------------------------------------------------------ 7 ----
    def test_a_prop_top_enters_the_support_table_with_a_prop_surface_id(self):
        surface = resolve((0.0, 0.9, 0.0), 0.35, step_limit(0.35),
                          props=[box((-0.5, 0.0, -0.5), (0.5, 0.35, 0.5))])
        self.assertEqual(surface.surface_id, FIRST_PROP_SURFACE_ID)
        self.assertTrue(is_prop_surface_id(surface.surface_id))
        self.assertEqual(prop_surface_index(surface.surface_id), 0)
        self.assertAlmostEqual(surface.height, 0.35, delta=TOLERANCE)
        self.assertEqual(surface.point, (0.0, 0.35, 0.0))
        self.assertEqual(surface.normal, UP)
        self.assertEqual(surface.candidate_count, 2)

    # ------------------------------------------------------------ 8 ----
    def test_a_prop_footprint_edge_is_inclusive(self):
        props = [box((0.0, 0.0, 0.0), (1.0, 0.2, 1.0))]
        self.assertEqual(resolve((1.0, 1.0, 0.0), 0.0, 0.275, props=props).surface_id,
                         FIRST_PROP_SURFACE_ID)
        self.assertEqual(resolve((0.0, 1.0, 1.0), 0.0, 0.275, props=props).surface_id,
                         FIRST_PROP_SURFACE_ID)

    # ------------------------------------------------------------ 9 ----
    def test_a_prop_outside_its_footprint_falls_back_to_the_ground(self):
        props = [box((0.0, 0.0, 0.0), (1.0, 0.2, 1.0))]
        surface = resolve((1.0001, 1.0, 0.0), 0.0, 0.275, props=props)
        self.assertEqual(surface.surface_id, GROUND_SURFACE_ID)
        self.assertAlmostEqual(surface.height, 0.0, delta=TOLERANCE)

    # ----------------------------------------------------------- 10 ----
    def test_a_prop_above_the_step_band_is_not_a_candidate(self):
        surface = resolve((0.0, 0.5, 0.0), 0.0, step_limit(1.10),
                          props=[box((-1.0, 0.0, -1.0), (1.0, 1.0, 1.0))])
        self.assertEqual(surface.surface_id, GROUND_SURFACE_ID)
        self.assertAlmostEqual(surface.height, 0.0, delta=TOLERANCE)
        self.assertEqual(surface.candidate_count, 1)

    # ----------------------------------------------------------- 11 ----
    def test_a_box_and_a_prop_at_the_same_height_keep_the_box(self):
        boxes = [box((-1.0, 0.0, -1.0), (1.0, 0.2, 1.0))]
        props = [box((-0.5, 0.0, -0.5), (0.5, 0.2, 0.5))]
        surface = resolve((0.0, 1.0, 0.0), 0.0, 0.4, boxes=boxes, props=props)
        self.assertEqual(surface.surface_id, 1)

        taller = [box((-0.5, 0.0, -0.5), (0.5, 0.3, 0.5))]
        surface = resolve((0.0, 1.0, 0.0), 0.0, 0.4, boxes=boxes, props=taller)
        self.assertEqual(surface.surface_id, FIRST_PROP_SURFACE_ID)
        self.assertAlmostEqual(surface.height, 0.3, delta=TOLERANCE)

    # ----------------------------------------------------------- 12 ----
    def test_support_selection_reads_exactly_the_reported_prop_count(self):
        props = [box((-1.0, 0.0, -1.0), (1.0, 0.01 * (i + 1), 1.0)) for i in range(7)]
        surface = resolve((0.0, 1.0, 0.0), 0.0, 0.5, props=props,
                          prop_count=MAX_SUPPORT_PROPS)
        self.assertEqual(surface.surface_id, FIRST_PROP_SURFACE_ID + 5)
        self.assertAlmostEqual(surface.height, 0.06, delta=TOLERANCE)

        self.assertTrue(is_support_prop_count_allowed(0))
        self.assertTrue(is_support_prop_count_allowed(6))
        self.assertFalse(is_support_prop_count_allowed(7))
        self.assertFalse(is_support_prop_count_allowed(-1))
        self.assertEqual(MAX_SUPPORT_PROPS, 6)

    # ----------------------------------------------------------- 13 ----
    def test_an_absorbed_support_prop_starts_the_gravity_fall(self):
        props = [box((-0.5, 0.0, -0.5), (0.5, 0.35, 0.5))]
        surface = resolve((0.0, 0.9, 0.0), 0.35, step_limit(0.35), props=props)
        self.assertEqual(surface.surface_id, FIRST_PROP_SURFACE_ID)
        previous = surface.height

        surface = resolve((0.0, 0.9, 0.0), previous, step_limit(0.35))
        self.assertEqual(surface.surface_id, GROUND_SURFACE_ID)
        self.assertAlmostEqual(surface.height, 0.0, delta=TOLERANCE)

        self.assertTrue(support_dropped_away(surface.height, previous, 0.01, False))
        self.assertTrue(resolve_fall_state(False, True, False, False))

        speed, budget = try_integrate_fall(0.0, 9.81, 0.02, 40.0)
        self.assertGreater(speed, 0.0)
        self.assertGreater(budget, 0.0)
        self.assertLess(budget, previous)

    # ----------------------------------------------------------- 14 ----
    def test_an_empty_prop_table_leaves_the_pre_p2_surface(self):
        boxes = [box((-1.0, 0.0, -1.0), (1.0, 0.2, 1.0))]
        props = [box((-1.0, 0.0, -1.0), (1.0, 0.15, 1.0))]
        on = resolve((0.0, 1.0, 0.0), 0.0, 0.4, boxes=boxes, props=props, prop_count=0)
        off = resolve((0.0, 1.0, 0.0), 0.0, 0.4, boxes=boxes)
        self.assertEqual(on, off)
        self.assertEqual(on.surface_id, 1)
        self.assertAlmostEqual(on.height, 0.2, delta=TOLERANCE)

    # ----------------------------------------------------------- 15 ----
    def test_non_finite_prop_is_refused_and_nothing_is_written(self):
        with self.assertRaises(ValueError):
            resolve((0.0, 1.0, 0.0), 0.0, 0.3,
                    props=[box((float("nan"), 0.0, 0.0), (1.0, 0.2, 1.0))])

        with self.assertRaises(ValueError):
            resolve((0.0, 1.0, 0.0), 0.0, 0.3,
                    props=[box((0.0, 0.0, 0.0), (-1.0, 0.2, 1.0))])


if __name__ == "__main__":
    unittest.main()
