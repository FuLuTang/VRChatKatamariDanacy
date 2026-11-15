"""Tests for the ball-contact v2 reference model.

They pin exactly the claims ``Docs/Design/BallMotor/CONTACT_MODEL_V2.md`` makes:
an exact sphere-vs-OBB closest point (face / edge / corner), no pass-through for
a ramp slab from the side or from below at the maximum step travel, the 45 deg
seam rise that v1 deadlocks on, a 90 deg climb capped at H_cap, a 70 deg slope
that is slower than a rideable one, the substepping bound at the smallest ball,
the curved-ramp chord budget and the near-window contract.

Run with ``python3 -m unittest discover -s Docs/tests`` from the repo root.
"""
import math
import unittest

from Docs.Design.BallMotor.contact_v2.contact_v2 import (
    CLASS_CLIMB,
    CLASS_RIDE,
    FEATURE_CORNER,
    FEATURE_EDGE,
    FEATURE_FACE,
    Obb,
    Plane,
    ROLE_BOX,
    ROLE_SLAB,
    State,
    World,
    classify_contact,
    climb_height_cap,
    climb_speed,
    closest_point_on_obb,
    curve_chord_budget,
    max_substeps,
    required_speed,
    select_window,
    sphere_obb_contact,
    step,
    tessellate_vertical_curve,
)

SIN45 = math.sin(math.radians(45.0))
COS45 = math.cos(math.radians(45.0))


def unit_box(surface_id=1, half=1.0):
    return Obb((0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0),
               (half, half, half), surface_id, ROLE_BOX)


def seam_world():
    """C05 geometry: a 45 deg slab whose top face meets a 0.6 m deck top.

    The slab's top face runs from (z=7.6, y=0) to (z=8.2, y=0.6), 4 m wide,
    0.04 m thick.  The deck occupies z in [8.2, 9.7] with its top at y=0.6.
    """
    normal = (0.0, COS45, -SIN45)          # up and back down the slope
    tangent = (0.0, SIN45, COS45)          # uphill
    top_mid = (0.0, 0.3, 7.9)
    centre = (top_mid[0] - normal[0] * 0.02,
              top_mid[1] - normal[1] * 0.02,
              top_mid[2] - normal[2] * 0.02)
    ramp = Obb(centre, (1.0, 0.0, 0.0), normal, tangent, (2.0, 0.02, 0.4245), 10, ROLE_SLAB)
    deck = Obb((0.0, 0.3, 8.95), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0),
               (2.0, 0.3, 0.75), 1, ROLE_BOX)
    return World(obbs=[ramp, deck], ground=Plane((0.0, 1.0, 0.0), 0.0))


def slope_world(angle_deg, rise_m, width_m=2.0):
    """A slab rising ``rise_m`` at ``angle_deg`` from a foot at (0, 0, 7) + a deck."""
    angle = math.radians(angle_deg)
    normal = (0.0, math.cos(angle), -math.sin(angle))
    tangent = (0.0, math.sin(angle), math.cos(angle))
    run = rise_m / math.tan(angle)
    along = rise_m / math.sin(angle)
    mid = (0.0, rise_m / 2.0, 7.0 + run / 2.0)
    slab = Obb((mid[0] - normal[0] * 0.05, mid[1] - normal[1] * 0.05,
                mid[2] - normal[2] * 0.05),
               (1.0, 0.0, 0.0), normal, tangent, (width_m / 2.0, 0.05, along / 2.0),
               20, ROLE_SLAB)
    deck = Obb((0.0, rise_m - 0.3, 7.0 + run + 0.6),
               (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0),
               (width_m / 2.0, 0.3, 0.6), 2, ROLE_BOX)
    return World(obbs=[slab, deck], ground=Plane((0.0, 1.0, 0.0), 0.0))


class ClosestPointTests(unittest.TestCase):
    def test_face_edge_and_corner_use_the_true_normal(self):
        box = unit_box()
        face_point, face_distance, face_normal, face_feature = closest_point_on_obb(
            box, (2.0, 0.5, 0.0))
        self.assertEqual(face_feature, FEATURE_FACE)
        self.assertAlmostEqual(face_distance, 1.0)
        self.assertEqual(face_point, (1.0, 0.5, 0.0))
        self.assertGreater(face_normal[0], 0.9999)

        edge_point, edge_distance, edge_normal, edge_feature = closest_point_on_obb(
            box, (2.0, 2.0, 0.0))
        self.assertEqual(edge_feature, FEATURE_EDGE)
        self.assertAlmostEqual(edge_distance, math.sqrt(2.0))
        self.assertEqual(edge_point, (1.0, 1.0, 0.0))
        self.assertAlmostEqual(edge_normal[0], 1.0 / math.sqrt(2.0))
        self.assertAlmostEqual(edge_normal[1], 1.0 / math.sqrt(2.0))

        corner_point, corner_distance, corner_normal, corner_feature = closest_point_on_obb(
            box, (2.0, 2.0, 2.0))
        self.assertEqual(corner_feature, FEATURE_CORNER)
        self.assertAlmostEqual(corner_distance, math.sqrt(3.0))
        self.assertEqual(corner_point, (1.0, 1.0, 1.0))
        for axis in range(3):
            self.assertAlmostEqual(corner_normal[axis], 1.0 / math.sqrt(3.0))

    def test_inside_point_pushes_out_through_the_nearest_face(self):
        contact = sphere_obb_contact(unit_box(), (0.9, 0.0, 0.0), 0.5)
        self.assertIsNotNone(contact)
        self.assertGreater(contact.depth, 0.5)  # radius plus the 0.1 face gap
        self.assertGreater(contact.normal[0], 0.9999)

    def test_a_box_corner_is_rounded_not_a_square_wall(self):
        # A corner contact exists while the centre is still outside the AABB.
        box = unit_box()
        contact = sphere_obb_contact(box, (1.2, 1.2, 0.0), 0.3)
        self.assertIsNotNone(contact)
        self.assertGreater(contact.normal[0], 0.5)
        self.assertGreater(contact.normal[1], 0.5)
        self.assertLess(contact.angle_deg, 50.0)  # a rounded edge, not a wall


class ClassificationTests(unittest.TestCase):
    def test_three_bands_are_derived_from_one_normal(self):
        self.assertEqual(classify_contact((0.0, 1.0, 0.0)), CLASS_RIDE)
        self.assertEqual(classify_contact((0.0, COS45, -SIN45)), CLASS_RIDE)
        self.assertEqual(classify_contact((0.0, math.cos(math.radians(70.0)),
                                           -math.sin(math.radians(70.0)))), CLASS_CLIMB)
        self.assertEqual(classify_contact((0.0, 0.0, 1.0)), CLASS_CLIMB)
        self.assertEqual(classify_contact((0.0, -1.0, 0.0)), 0)  # ceiling


class SubstepTests(unittest.TestCase):
    def test_smallest_ball_at_max_speed_needs_two_substeps(self):
        # 4 m/s * 0.02 s = 0.08 m; r = 0.15 m; kappa 0.5 -> 2 substeps.
        self.assertEqual(max_substeps((0.08, 0.0, 0.0), 0.15), 2)
        self.assertLessEqual(0.08 / 2.0, 0.5 * 0.15)
        # Dash = 1.6x keeps the same count with the margin still under kappa*r.
        self.assertEqual(max_substeps((0.128, 0.0, 0.0), 0.15), 2)
        self.assertLessEqual(0.128 / 2.0, 0.5 * 0.15)

    def test_no_tunnel_bound_is_four_times_the_needed_margin(self):
        # The proof only needs a sample spacing <= r; kappa 0.5 gives 4x.
        self.assertLessEqual(0.5, 1.0)
        for radius in (0.15, 0.30, 0.55, 1.10):
            travel = 4.0 * 0.02
            n = max_substeps((travel, 0.0, 0.0), radius)
            self.assertLessEqual(travel / n, radius)
            self.assertLessEqual(n, 2)  # no current ball size needs more than 2

    def test_fall_displacement_is_cost_capped(self):
        self.assertEqual(max_substeps((0.8, 0.0, 0.0), 0.15), 8)


class SolidSlabTests(unittest.TestCase):
    def _thick_side_slab(self):
        # The 0.04 m board of C05 is thinner than the ball is tall, so a
        # ground-level approach meets its underside edge (also a block).  For a
        # clean side-face test use the same 45 deg top face with a 0.6 m body.
        normal = (0.0, COS45, -SIN45)
        tangent = (0.0, SIN45, COS45)
        top_mid = (0.0, 0.3, 7.9)
        centre = (top_mid[0] - normal[0] * 0.3, top_mid[1] - normal[1] * 0.3,
                  top_mid[2] - normal[2] * 0.3)
        ramp = Obb(centre, (1.0, 0.0, 0.0), normal, tangent, (2.0, 0.3, 0.4245), 10,
                   ROLE_SLAB)
        return World(obbs=[ramp], ground=Plane((0.0, 1.0, 0.0), 0.0)), ramp

    def test_ramp_side_stops_the_ball_at_max_step_travel(self):
        world, ramp = self._thick_side_slab()
        state = State(center=(3.0, 0.15, 7.9), radius=0.15, v_planar=(-4.0, 0.0, 0.0))
        for _ in range(30):
            report = step(world, state, (-1.0, 0.0, 0.0), 4.0, 0.02)
        # Side face at x = 2; the ball must stay one radius outside it.
        self.assertGreaterEqual(state.center[0], 2.0 + 0.15 - 0.002)
        self.assertLessEqual(report.residual, 0.002)
        _, distance, _, _ = closest_point_on_obb(ramp, state.center)
        self.assertGreaterEqual(distance, 0.15 - 0.002)

    def test_support_only_ramp_would_pass_through_from_the_side(self):
        # Negative control: with the ramp absent from the solids (the v1 model)
        # the same drive walks straight through the board's footprint.
        world = World(obbs=[], ground=Plane((0.0, 1.0, 0.0), 0.0))
        state = State(center=(3.0, 0.15, 7.9), radius=0.15, v_planar=(-4.0, 0.0, 0.0))
        for _ in range(30):
            step(world, state, (-1.0, 0.0, 0.0), 4.0, 0.02)
        self.assertLess(state.center[0], 1.0)

    def test_ramp_underside_stops_an_upward_ball(self):
        world = seam_world()
        # Lift the ramp 1 m so the ball can rise into its underside.
        ramp_low = world.obbs[0]
        lifted = Obb((ramp_low.center[0], ramp_low.center[1] + 1.0, ramp_low.center[2]),
                     ramp_low.axis_x, ramp_low.axis_y, ramp_low.axis_z,
                     ramp_low.half_extents, ramp_low.surface_id, ramp_low.role)
        world = World(obbs=[lifted], ground=Plane((0.0, 1.0, 0.0), 0.0))
        state = State(center=(0.0, 0.5, 7.9), radius=0.15, v_planar=(0.0, 0.0, 0.0),
                      v_y=4.0)
        highest = 0.0
        minimum_distance = 1e9
        for _ in range(60):
            step(world, state, (0.0, 0.0, 1.0), 4.0, 0.02)
            highest = max(highest, state.center[1])
            _, distance, _, _ = closest_point_on_obb(lifted, state.center)
            minimum_distance = min(minimum_distance, distance)
        # The underside is at y = 1.272 near z=7.9; the centre must never come
        # out above the slab's top face (y = 1.3), and it never penetrates.
        self.assertLess(highest, 1.30)
        self.assertGreaterEqual(minimum_distance, 0.15 - 0.002)


class SeamTests(unittest.TestCase):
    def test_45_degree_seam_ball_reaches_the_deck(self):
        world = seam_world()
        start_y = 0.1 + 0.15 * SIN45
        start_z = 7.7 - 0.15 * SIN45
        state = State(center=(0.0, start_y, start_z), radius=0.15,
                      v_planar=(0.0, 0.0, 4.0))
        for _ in range(20):
            step(world, state, (0.0, 0.0, 1.0), 4.0, 0.02)
        self.assertGreater(state.center[2], 8.3)      # past the deck edge
        self.assertAlmostEqual(state.center[1], 0.75, delta=0.002)  # 0.6 + r
        # The v1 inflated-AABB stop point was z = 8.2 - r = 8.05; being past it
        # is exactly what the OBB contact buys.
        self.assertGreater(state.center[2], 8.2 - 0.15)

    def test_45_degree_seam_keeps_the_planar_speed(self):
        world = seam_world()
        state = State(center=(0.0, 0.1 + 0.15 * SIN45, 7.7 - 0.15 * SIN45),
                      radius=0.15, v_planar=(0.0, 0.0, 4.0))
        for _ in range(20):
            step(world, state, (0.0, 0.0, 1.0), 4.0, 0.02)
        self.assertAlmostEqual(state.v_planar[2], 4.0, delta=0.05)


class ClimbTests(unittest.TestCase):
    def test_height_cap_is_one_diameter_and_adds_protrusion(self):
        for radius in (0.15, 0.30, 0.55, 1.10):
            self.assertAlmostEqual(climb_height_cap(radius), 2.0 * radius)
        self.assertAlmostEqual(climb_height_cap(0.30, protrusion_m=0.2), 0.8)

    def test_gate_is_continuous_and_steeper_means_more_speed(self):
        self.assertLess(required_speed(50.0), required_speed(70.0))
        self.assertLess(required_speed(70.0), required_speed(90.0))
        self.assertAlmostEqual(required_speed(90.0), 1.5)
        self.assertLess(climb_speed(1.0, 2.0, 70.0, 0.0, 0.6),
                        climb_speed(1.0, 2.0, 50.0, 0.0, 0.6))
        self.assertLess(climb_speed(1.0, 2.0, 90.0, 0.0, 0.6), 0.25)

    def test_90_degree_wall_climbs_to_the_cap_then_slides_back(self):
        wall = Obb((0.0, 1.5, -0.5), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0),
                   (5.0, 1.5, 0.5), 1, ROLE_BOX)
        world = World(obbs=[wall], ground=Plane((0.0, 1.0, 0.0), 0.0))
        state = State(center=(0.0, 0.30, 0.5), radius=0.30, v_planar=(0.0, 0.0, -4.0))
        highest = 0.0
        for _ in range(500):
            step(world, state, (0.0, 0.0, -1.0), 4.0, 0.02)
            highest = max(highest, state.center[1])
        cap = climb_height_cap(0.30)
        self.assertLessEqual(highest - 0.30, cap + 0.02)
        self.assertGreater(highest - 0.30, 0.45)      # it really did climb
        self.assertLess(state.center[1], 0.35)        # and it fell back down
        self.assertTrue(state.climb_locked)           # no silent climb loop
        self.assertAlmostEqual(state.center[2], 0.30, delta=0.01)  # stayed on the face

    def test_70_degree_slope_is_climbable_but_slower(self):
        world = slope_world(70.0, 0.24, width_m=2.0)
        state = State(center=(0.0, 0.15, 6.65), radius=0.15, v_planar=(0.0, 0.0, 4.0))
        highest = 0.0
        for _ in range(200):
            step(world, state, (0.0, 0.0, 1.0), 4.0, 0.02, drive_accel=5.0)
            highest = max(highest, state.center[1])
        self.assertAlmostEqual(highest, 0.24 + 0.15, delta=0.01)  # on the 0.24 m deck
        # A rideable 45 deg ramp at the same 4 m/s rises at 4 m/s; the climb is
        # bounded by the climb speed, well under 1 m/s at 70 deg.
        self.assertLess(climb_speed(1.0, 2.0, 70.0, 0.0, 0.3), 1.0)
        self.assertGreater(climb_speed(1.0, 2.0, 70.0, 0.0, 0.3), 0.2)

    def test_50_degree_slab_is_climbed_only_within_the_cap(self):
        # C06 redesigned: 50 deg is in the climb band, and a 0.6 m rise against
        # H_cap = D separates the sizes.  D=0.30 caps at 0.30 and slides back;
        # D=1.10 reaches the 0.6 deck.
        small = slope_world(50.0, 0.6, width_m=3.0)
        small_state = State(center=(0.0, 0.15, 6.4), radius=0.15,
                            v_planar=(0.0, 0.0, 4.0))
        small_highest = 0.0
        for _ in range(250):
            step(small, small_state, (0.0, 0.0, 1.0), 4.0, 0.02, drive_accel=5.0)
            small_highest = max(small_highest, small_state.center[1])
        self.assertLessEqual(small_highest - 0.15, climb_height_cap(0.15) + 0.02)
        self.assertLess(small_state.center[1] - 0.15, 0.05)   # slid back down
        self.assertTrue(small_state.climb_locked)

        big = slope_world(50.0, 0.6, width_m=3.0)
        big_state = State(center=(0.0, 0.55, 6.4), radius=0.55, v_planar=(0.0, 0.0, 4.0))
        big_highest = 0.0
        for _ in range(250):
            step(big, big_state, (0.0, 0.0, 1.0), 4.0, 0.02, drive_accel=5.0)
            big_highest = max(big_highest, big_state.center[1])
        self.assertGreater(big_highest - 0.55, 0.6 - 0.01)     # on the 0.6 deck

    def test_low_wall_is_rolled_over(self):
        # 0.5 D wall (0.15 m for D=0.30): climb + rounded top edge gets it over.
        wall = Obb((0.0, 0.075, -1.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0),
                   (5.0, 0.075, 1.0), 1, ROLE_BOX)
        world = World(obbs=[wall], ground=Plane((0.0, 1.0, 0.0), 0.0))
        state = State(center=(0.0, 0.15, 0.8), radius=0.15, v_planar=(0.0, 0.0, -4.0))
        highest = 0.0
        for _ in range(150):
            step(world, state, (0.0, 0.0, -1.0), 4.0, 0.02, drive_accel=5.0)
            highest = max(highest, state.center[1])
        self.assertAlmostEqual(highest, 0.30, delta=0.01)  # r above the wall top
        self.assertLess(state.center[2], -2.0)             # crossed to the far side


class CurvedRampTests(unittest.TestCase):
    def test_twelve_segments_per_90_degrees_meets_the_chord_budget(self):
        obbs, metrics = tessellate_vertical_curve(
            (0.0, 0.0, 7.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0),
            radius_m=3.0, arc_deg=90.0, segments=12, width_m=4.0, thickness_m=0.1)
        self.assertEqual(len(obbs), 12)
        self.assertAlmostEqual(metrics["delta_deg"], 7.5)
        self.assertAlmostEqual(metrics["normal_jump_deg"], 3.75)
        self.assertLessEqual(metrics["sagitta_m"], 0.05 * 0.15)  # 5% of the smallest r
        self.assertTrue(curve_chord_budget(3.0, 12, 90.0, 0.15))
        self.assertFalse(curve_chord_budget(3.0, 4, 90.0, 0.15))
        # No crack on the convex side: each segment is longer than its chord.
        self.assertGreater(metrics["overlap"], 1.0)

    def test_curve_axes_are_orthonormal_and_the_chain_is_continuous(self):
        obbs, _ = tessellate_vertical_curve(
            (0.0, 0.0, 7.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0),
            radius_m=5.0, arc_deg=45.0, segments=8, width_m=3.0, thickness_m=0.2)
        for obb in obbs:
            for a, b in ((obb.axis_x, obb.axis_y), (obb.axis_x, obb.axis_z),
                         (obb.axis_y, obb.axis_z)):
                self.assertAlmostEqual(sum(a[i] * b[i] for i in range(3)), 0.0, places=9)
        for first, second in zip(obbs, obbs[1:]):
            gap = math.sqrt(sum((second.center[i] - first.center[i]) ** 2 for i in range(3)))
            # Segment centres are one chord apart; the overlap must cover the joint.
            self.assertLess(gap, 2.0 * max(first.half_extents[2], second.half_extents[2]))

    def test_ball_traverses_a_shallow_curve_without_stalling(self):
        obbs, _ = tessellate_vertical_curve(
            (0.0, 0.0, 7.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0),
            radius_m=10.0, arc_deg=30.0, segments=8, width_m=3.0, thickness_m=0.2)
        world = World(obbs=obbs, ground=Plane((0.0, 1.0, 0.0), 0.0))
        state = State(center=(0.0, 0.15, 7.0), radius=0.15, v_planar=(4.0, 0.0, 0.0))
        highest = 0.0
        for _ in range(90):
            step(world, state, (1.0, 0.0, 0.0), 4.0, 0.02, drive_accel=5.0)
            highest = max(highest, state.center[1])
        # 30 deg over R=10 rises 1.34 m; the centre rides r*cos30 above it.
        self.assertGreater(highest, 1.40)
        self.assertGreater(state.center[0], 4.0)


class WindowTests(unittest.TestCase):
    def _row(self, count, spacing=2.0):
        obbs = []
        for i in range(count):
            obbs.append(Obb((i * spacing, 0.5, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0),
                            (0.0, 0.0, 1.0), (0.4, 0.4, 0.4), i + 1, ROLE_BOX))
        return obbs

    def test_only_near_geometry_enters_the_window(self):
        obbs = self._row(200)
        window = select_window(obbs, (0.0, 0.5, 0.0), 0.30, 0.13)
        self.assertLessEqual(len(window.items), 24)
        self.assertTrue(all(abs(item.center[0]) <= 3.0 for item in window.items))
        self.assertEqual(window.dropped, 0)
        self.assertTrue(window.complete)
        self.assertTrue(window.rebuilt)

    def test_overflow_is_explicit_and_reports_incomplete(self):
        # 40 boxes all within contact reach of the ball: capacity 24 overflows.
        obbs = []
        for i in range(40):
            angle = 2.0 * math.pi * i / 40.0
            obbs.append(Obb((0.35 * math.cos(angle), 0.35, 0.35 * math.sin(angle)),
                            (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0),
                            (0.2, 0.2, 0.2), i + 1, ROLE_BOX))
        window = select_window(obbs, (0.0, 0.35, 0.0), 0.30, 0.13, capacity=24)
        self.assertEqual(len(window.items), 24)
        self.assertEqual(window.dropped, 16)
        self.assertFalse(window.complete)  # dropped item could have been touching

    def test_hysteresis_keeps_a_boundary_item_only_while_it_stays_near(self):
        far = Obb((1.00, 0.5, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0),
                  (0.2, 0.2, 0.2), 7, ROLE_BOX)
        # radius_in = 0.30 + 0.13 + 0.05 = 0.48; radius_out = 0.98.
        # The box surface is 0.8 m from the ball centre: inside radius_out only.
        fresh = select_window([far], (0.0, 0.5, 0.0), 0.30, 0.13, previous_ids=())
        kept = select_window([far], (0.0, 0.5, 0.0), 0.30, 0.13, previous_ids=(7,))
        self.assertEqual(fresh.items, [])
        self.assertEqual(len(kept.items), 1)
        self.assertFalse(kept.rebuilt)


if __name__ == "__main__":
    unittest.main()
