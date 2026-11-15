"""Tests for the derived ride geometry (Docs/Design/BallLayers/FollowRig/ride_geometry.py).

Two jobs: pin the shipped model so the reported bug cannot come back silently, and pin the
proposed model's invariants. Expectation values are hand-derived in the comments (closed forms
and the owner's measurement), never copied from the module under test.
"""
import math
import unittest

import Docs.Design.BallLayers.FollowRig.ride_geometry as rg

# The motor's shipped eye coefficients, written as literals on purpose
# (KatamariBallMotor.cs:250-257): base up, per radius up, base back, per radius back.
SHIPPED_EYE = (0.55, 2.4, 0.75, 2.2)

# The owner's report, 2026-11-15: 951x431 view, ball ~40 deg below the crosshair, ball centre
# 99 px below the bottom edge.
OWNER_VIEW_W, OWNER_VIEW_H = 951, 431
OWNER_BALL_CENTRE_DEG = 40.0
OWNER_PX_BELOW_BOTTOM = 99.0

PRODUCT_RADII = (0.15, 0.30, 0.55, 1.10)


def _shipped(radius):
    return rg.legacy_eye(radius, *SHIPPED_EYE)


class ShippedModelTests(unittest.TestCase):
    """The 2026-11-15 linear model, i.e. the state the owner played."""

    def test_reproduces_the_owner_measurement(self):
        # h = 0.55 + 2.4*0.15 = 0.91, d = 0.75 + 2.2*0.15 = 1.08 from the ball centre, so the
        # centre is atan(0.91/1.08) = 40.12 deg below the horizon.
        geometry = _shipped(0.15)
        self.assertAlmostEqual(geometry.up_m, 0.91, delta=1e-12)
        self.assertAlmostEqual(geometry.back_m, 1.08, delta=1e-12)
        self.assertAlmostEqual(geometry.alpha_deg, math.degrees(math.atan2(0.91, 1.08)),
                               delta=1e-12)
        self.assertAlmostEqual(geometry.alpha_deg, OWNER_BALL_CENTRE_DEG, delta=0.5)

        # Pinhole projection at the default 60 deg vertical FOV: (H/2)*tan(40.12)/tan(30)
        # = 215.5 * 0.8418 / 0.5774 = 314.2 px from the top, i.e. 98.7 px BELOW the bottom of a
        # 431 px frame. That is the owner's 99 px, so the report was taken at 60 deg FOV.
        on_screen = rg.ball_on_screen(geometry, OWNER_VIEW_H, 30.0)
        self.assertAlmostEqual(on_screen.centre_px_below_bottom, OWNER_PX_BELOW_BOTTOM, delta=0.5)
        self.assertFalse(on_screen.centre_visible)

        # And the whole SILHOUETTE is outside. Its upper edge is the tangent, so it sits at
        # alpha - beta = 40.117 - 6.097 = 34.02 deg (the sphere's geometric top point projects
        # deeper, atan((0.91-0.15)/1.08) = 35.13 deg, but that is inside the silhouette). Both
        # are past the 30 deg bottom edge of a 60 deg frame.
        self.assertAlmostEqual(geometry.top_deg,
                               geometry.alpha_deg - geometry.beta_deg, delta=1e-12)
        self.assertAlmostEqual(geometry.top_deg, 34.0202, delta=1e-3)
        self.assertGreater(geometry.top_deg, 30.0)
        self.assertGreater(on_screen.top_px_below_bottom, 0.0)
        self.assertAlmostEqual(geometry.clearance_above_surface_m, 0.91 - 0.15, delta=1e-12)

    def test_angle_is_pinned_near_forty_degrees_at_every_size(self):
        # alpha(r) = atan((a + b r)/(c + d r)) is a Mobius function of r. b*c - a*d =
        # 2.4*0.75 - 0.55*2.2 = 0.59 > 0, so it is strictly increasing and bounded by its
        # endpoints: atan(a/c) = atan(0.55/0.75) = 36.25 deg at r -> 0 and atan(b/d) =
        # atan(2.4/2.2) = 47.49 deg as r -> infinity. The ratio of the two linear terms is
        # nearly constant (2.4/2.2 = 1.09), so the framing never depends on the ball's size.
        self.assertAlmostEqual(_shipped(1e-12).alpha_deg, 36.2538, delta=1e-3)
        self.assertAlmostEqual(_shipped(1e9).alpha_deg, 47.4896, delta=1e-3)
        self.assertGreater(2.4 * 0.75 - 0.55 * 2.2, 0.0)

        previous = _shipped(1e-12).alpha_deg
        for radius in (0.05, 0.15, 0.30, 0.55, 1.10, 2.20, 15.0):
            alpha = _shipped(radius).alpha_deg
            self.assertGreater(alpha, previous)          # strictly increasing
            self.assertLessEqual(alpha, 47.4896 + 1e-3)  # and bounded
            previous = alpha
            # The point of the algebra: no radius puts the ball inside a 60 deg frame.
            self.assertGreater(alpha, 30.0)

    def test_seat_would_be_underground_at_the_small_size(self):
        # Seat = eye - measured seated head (ClientSim 1.204159 m), so with the shipped
        # constants: 0.15 + 0.91 - 1.204159 = -0.144 m. The bottom of the range buries the
        # rider; that is the second half of "the seat is not on the ball".
        seat = rg.seat_for(0.15, _shipped(0.15), rg.MEASURED_SEATED_HEAD_ABOVE_SEAT_M)
        self.assertAlmostEqual(seat.above_ground_m, 0.15 + 0.91 - 1.204159, delta=1e-12)
        self.assertLess(seat.above_ground_m, 0.0)


class ProposedModelTests(unittest.TestCase):
    """The derived model: constant alpha, saturating beta, human eye floor."""

    def test_constants_are_derived_not_picked(self):
        frame = rg.Frame()
        # beta is a third of the frame's half-height, alpha leaves the bottom margin, the floor
        # is a measured head plus a seat clearance.
        self.assertAlmostEqual(frame.beta_deg, frame.half_fov_deg / 3.0, delta=1e-12)
        self.assertAlmostEqual(frame.alpha_deg,
                               frame.half_fov_deg - frame.beta_deg - frame.bottom_margin_deg,
                               delta=1e-12)
        self.assertAlmostEqual(frame.eye_floor_m,
                               rg.MEASURED_SEATED_HEAD_ABOVE_SEAT_M + rg.SEAT_GROUND_CLEARANCE_M,
                               delta=1e-12)
        # The slopes are the closed form h = r sin(alpha)/sin(beta), d = r cos(alpha)/sin(beta).
        alpha = math.radians(frame.alpha_deg)
        beta = math.radians(frame.beta_deg)
        self.assertAlmostEqual(frame.up_per_radius, math.sin(alpha) / math.sin(beta), delta=1e-12)
        self.assertAlmostEqual(frame.back_per_radius, math.cos(alpha) / math.sin(beta),
                               delta=1e-12)
        self.assertAlmostEqual(frame.alpha_deg, 18.0, delta=1e-9)
        self.assertAlmostEqual(frame.beta_deg, 10.0, delta=1e-9)

        # Nothing is baked: a narrower frame moves beta, alpha and the crossover together.
        narrow = rg.Frame(half_fov_deg=24.0)
        self.assertAlmostEqual(narrow.beta_deg, 8.0, delta=1e-12)
        self.assertAlmostEqual(narrow.alpha_deg, 14.0, delta=1e-12)
        self.assertNotAlmostEqual(narrow.crossover_radius_m, frame.crossover_radius_m, places=6)

    def test_alpha_is_constant_and_beta_saturates(self):
        frame = rg.Frame()
        previous_beta = -1.0
        for radius in (0.12, 0.15, 0.30, 0.45, 0.55, 1.10, 5.0, 15.0):
            geometry = rg.eye_for_radius(radius, frame)
            self.assertAlmostEqual(geometry.alpha_deg, frame.alpha_deg, delta=1e-9)
            self.assertLessEqual(geometry.beta_deg, frame.beta_deg + 1e-12)
            self.assertGreaterEqual(geometry.beta_deg, previous_beta - 1e-12)
            previous_beta = geometry.beta_deg
            if radius >= frame.crossover_radius_m:
                self.assertAlmostEqual(geometry.beta_deg, frame.beta_deg, delta=1e-9)
        # The framing branch is the one that reaches the frame's third; below the crossover the
        # ball is smaller than that because a human eye cannot get closer.
        self.assertLess(rg.eye_for_radius(0.15, frame).beta_deg, frame.beta_deg)

    def test_rails_can_rescale_but_never_move_the_ball(self):
        # d is DERIVED from h, so d/h = 1/tan(alpha) for every radius: clamping h (the height
        # rails) or scaling both (the back rail) changes the ball's apparent size only.
        frame = rg.Frame()
        expected = 1.0 / math.tan(math.radians(frame.alpha_deg))
        for radius in rg.PRODUCT_RADII_M:
            geometry = rg.eye_for_radius(radius, frame)
            self.assertAlmostEqual(geometry.back_m / geometry.up_m, expected, delta=1e-9)

    def test_ball_stays_in_frame_over_the_whole_size_range(self):
        frame = rg.Frame()
        radii = [0.12 + 0.02 * i for i in range(int((15.0 - 0.12) / 0.02) + 1)]
        for view_height in (OWNER_VIEW_H, 1080):
            for radius in radii:
                geometry = rg.eye_for_radius(radius, frame)
                for half_fov in (22.0, 24.0, 30.0, 40.0, 45.0):
                    on_screen = rg.ball_on_screen(geometry, view_height, half_fov)
                    # The hard requirement: the ball is in the picture, not below it.
                    self.assertTrue(on_screen.centre_visible,
                                    f"r={radius} half_fov={half_fov} centre outside frame")
                    # Nominal and wider frames hold the whole silhouette, bottom margin 2 deg.
                    if half_fov >= 30.0:
                        self.assertTrue(on_screen.silhouette_fully_visible,
                                        f"r={radius} half_fov={half_fov} silhouette clipped")

    def test_ball_never_crosses_the_horizon(self):
        # alpha > beta is the "lower part of the view" rule: the silhouette's top stays below
        # the horizon, so the ball never covers the sky or the aim line.
        frame = rg.Frame()
        self.assertGreater(frame.alpha_deg, frame.beta_deg)
        for radius in rg.PRODUCT_RADII_M:
            self.assertGreater(rg.eye_for_radius(radius, frame).top_deg, 0.0)

    def test_camera_is_always_outside_the_ball(self):
        # Distance = r/sin(beta) >= r/sin(10 deg) = 5.76 r, so near-clip cannot be inside the
        # ball at any size: the "camera inside a 30 m ball" failure of the shipped caps (8/8 m)
        # cannot happen here.
        frame = rg.Frame()
        for radius in rg.PRODUCT_RADII_M:
            geometry = rg.eye_for_radius(radius, frame)
            self.assertGreater(geometry.distance_m, radius)
            self.assertGreater(geometry.distance_m, 5.7 * radius)

    def test_seat_is_on_the_ground_at_the_small_size_and_climbs_with_the_ball(self):
        frame = rg.Frame()
        seat = rg.seat_for(0.15, rg.eye_for_radius(0.15, frame),
                           rg.MEASURED_SEATED_HEAD_ABOVE_SEAT_M)
        # floor branch: h = 1.254159 - 0.15, so the seat sits exactly on the platform height.
        self.assertAlmostEqual(seat.above_ground_m, rg.SEAT_GROUND_CLEARANCE_M, delta=1e-12)
        heights = [rg.seat_for(radius, rg.eye_for_radius(radius, frame),
                               rg.MEASURED_SEATED_HEAD_ABOVE_SEAT_M).above_ground_m
                   for radius in rg.PRODUCT_RADII_M]
        for lower, upper in zip(heights, heights[1:]):
            # Non-decreasing: the rider's platform climbs as the ball grows. Equal to float
            # noise in the floor branch, so compare with a tolerance rather than ordered().
            self.assertGreaterEqual(upper, lower - 1e-9)
        for height in heights:
            self.assertGreaterEqual(height, 0.0)

    def test_player_height_enters_only_through_the_eye_floor(self):
        # A 5 m player's seated head is ~0.85 * 5/1.7 = 2.5 m. The floor rises with it, so the
        # ball's place in the frame is unchanged and it is still visible; only its apparent
        # size shrinks where the human floor outruns the ball's own scale.
        tall = rg.Frame(seated_head_m=2.5)
        self.assertAlmostEqual(tall.eye_floor_m, 2.55, delta=1e-12)
        geometry = rg.eye_for_radius(0.15, tall)
        self.assertAlmostEqual(geometry.alpha_deg, tall.alpha_deg, delta=1e-9)
        self.assertTrue(rg.ball_on_screen(geometry, OWNER_VIEW_H, 30.0).centre_visible)
        self.assertGreater(tall.crossover_radius_m, rg.Frame().crossover_radius_m)
        self.assertLess(geometry.beta_deg, rg.Frame().beta_deg)

        # The bug a tall avatar can still cause: the shipped absolute plausibility rail rejects
        # the measurement, so the seat stays on the eye point and the camera rides 2.5 m above
        # it. atan((1.104+2.5)/3.398) = 46.7 deg is again outside the frame.
        refused = rg._geometry(0.15, rg.eye_for_radius(0.15).up_m + 2.5,
                               rg.eye_for_radius(0.15).back_m)
        self.assertGreater(refused.alpha_deg, 30.0)
        self.assertFalse(rg.ball_on_screen(refused, OWNER_VIEW_H, 30.0).centre_visible)

    def test_pinhole_projection_endpoints(self):
        self.assertAlmostEqual(rg.px_below_axis(0.0, 431.0, 30.0), 0.0, delta=1e-12)
        self.assertAlmostEqual(rg.px_below_axis(30.0, 431.0, 30.0), 215.5, delta=1e-9)
        self.assertAlmostEqual(rg.px_below_axis(-30.0, 431.0, 30.0), -215.5, delta=1e-9)
        with self.assertRaises(ValueError):
            rg.px_below_axis(10.0, 431.0, 0.0)


class TableTests(unittest.TestCase):
    """The design doc prints this table; keep it arithmetically honest."""

    def test_product_table_matches_the_closed_form(self):
        frame = rg.Frame()
        rows = rg.table(frame)
        self.assertEqual(len(rows), len(rg.PRODUCT_RADII_M))
        for row in rows:
            radius = row["radius_m"]
            closed_up = frame.up_per_radius * radius
            if closed_up >= frame.eye_floor_m - radius:
                self.assertAlmostEqual(row["up_m"], closed_up, delta=1e-9)
                self.assertAlmostEqual(row["back_m"], frame.back_per_radius * radius, delta=1e-9)
                self.assertAlmostEqual(row["beta_deg"], frame.beta_deg, delta=1e-9)
            else:
                self.assertAlmostEqual(row["up_m"], frame.eye_floor_m - radius, delta=1e-9)
            self.assertAlmostEqual(row["eye_above_ground_m"], radius + row["up_m"], delta=1e-12)

    def test_table_rows_at_the_start_and_full_size(self):
        rows = {row["radius_m"]: row for row in rg.table()}
        # r = 0.15 (start ball): floor branch. 1.254159 - 0.15 = 1.104159 up,
        # 1.104159/tan(18) = 3.39833 back, silhouette 2*asin(0.15*sin18/1.104159) = 4.81 deg.
        self.assertAlmostEqual(rows[0.15]["up_m"], 1.104159, delta=1e-9)
        self.assertAlmostEqual(rows[0.15]["back_m"], 1.104159 / math.tan(math.radians(18.0)),
                               delta=1e-9)
        self.assertAlmostEqual(rows[0.15]["beta_deg"], 2.4057, delta=1e-3)
        # r = 1.10 (full ball): framing branch, 1.779558 * 1.1 = 1.957514 up and 5.476916 * 1.1
        # = 6.024608 back, silhouette exactly 20 deg.
        self.assertAlmostEqual(rows[1.10]["up_m"], 1.779558 * 1.10, delta=1e-6)
        self.assertAlmostEqual(rows[1.10]["back_m"], 5.476916 * 1.10, delta=1e-6)
        self.assertAlmostEqual(rows[1.10]["screen_diameter_deg"], 20.0, delta=1e-6)


if __name__ == "__main__":
    unittest.main()
