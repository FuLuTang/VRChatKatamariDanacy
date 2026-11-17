"""Tests for the ride geometry (Docs/Design/BallLayers/FollowRig/ride_geometry.py).

Expectation values are hand-derived in the comments (closed forms and the owner's measurement),
never copied from the module under test. The C# twins (RideViewMath, RideScaleMath; Tests/EditMode/
Riding) pin the same numbers.
"""
import math
import unittest

import Docs.Design.BallLayers.FollowRig.ride_geometry as rg

OWNER_VIEW_H = 431
OWNER_BALL_CENTRE_DEG = 40.0
OWNER_PX_BELOW_BOTTOM = 99.0

DIAMETERS = (0.222, 0.5, 0.8, 2.0, 20.0)
KAPPAS = (0.2, 0.35, 0.5)


class ViewRuleTests(unittest.TestCase):
    def test_angles_and_slopes_are_derived(self):
        # beta = a third of the half field = 10 deg; alpha = 30 - 10 - 2 = 18 deg.
        self.assertAlmostEqual(rg.BETA_DEG, 10.0, delta=1e-12)
        self.assertAlmostEqual(rg.ALPHA_DEG, 18.0, delta=1e-12)
        # sin 18 / sin 10 = 0.309017 / 0.173648 = 1.779558; back divides by tan 18 = 0.324920.
        self.assertAlmostEqual(rg.UP_PER_RADIUS, 1.779558, delta=1e-6)
        self.assertAlmostEqual(rg.BACK_PER_RADIUS, 5.476916, delta=1e-5)

    def test_old_constants_reproduce_the_owner_measurement(self):
        # Old eye 0.55 + 2.4 r up, 0.75 + 2.2 r back at r = 0.15: 0.91 / 1.08 -> 40.12 deg, which
        # a pinhole at half FOV 30 puts 98.7 px below the bottom of a 431 px frame (owner: 99 px).
        alpha = math.degrees(math.atan2(0.55 + 2.4 * 0.15, 0.75 + 2.2 * 0.15))
        self.assertAlmostEqual(alpha, OWNER_BALL_CENTRE_DEG, delta=0.5)
        below_bottom = rg.px_below_axis(alpha, OWNER_VIEW_H, rg.HALF_FOV_DEG) - OWNER_VIEW_H / 2
        self.assertAlmostEqual(below_bottom, OWNER_PX_BELOW_BOTTOM, delta=0.5)

    def test_ball_view_is_the_same_picture_at_every_size(self):
        for diameter in DIAMETERS + (0.05, 30.0):
            ride = rg.ride_for(diameter, 0.35)
            self.assertAlmostEqual(ride.alpha_deg, 18.0, delta=1e-9)
            self.assertAlmostEqual(ride.beta_deg, 10.0, delta=1e-9)
            self.assertAlmostEqual(ride.bottom_deg, 28.0, delta=1e-9)
            # Pinhole: the lower edge stays inside the frame (28 < 30) at the 951x431 view.
            bottom_px = rg.px_below_axis(ride.bottom_deg, OWNER_VIEW_H, rg.HALF_FOV_DEG)
            self.assertLess(bottom_px, OWNER_VIEW_H / 2)
            # And the ball never crosses the horizon: top edge 8 deg below it.
            self.assertAlmostEqual(ride.top_deg, 8.0, delta=1e-9)
            # Proportional: every length divided by the diameter is a constant.
            self.assertAlmostEqual(ride.up_m / diameter, rg.UP_PER_RADIUS / 2.0, delta=1e-9)
            self.assertAlmostEqual(ride.back_m / diameter, rg.BACK_PER_RADIUS / 2.0, delta=1e-9)

    def test_camera_is_outside_the_ball(self):
        # Distance = r / sin 10 = 5.76 r.
        for diameter in DIAMETERS:
            ride = rg.ride_for(diameter, 0.35)
            self.assertAlmostEqual(math.hypot(ride.up_m, ride.back_m) / (diameter / 2.0),
                                   1.0 / math.sin(math.radians(10.0)), delta=1e-6)


class RiderSizeTests(unittest.TestCase):
    def test_unit_radius_hand_values(self):
        # The eye is up - r = 0.779558 r above the ball's top; E = 0.779558 r / kappa.
        self.assertAlmostEqual(rg.EYE_ABOVE_TOP_PER_RADIUS, 0.779558, delta=1e-6)
        self.assertAlmostEqual(rg.eye_above_waist(1.0), 0.779558, delta=1e-6)
        # D = 2: r = 1.
        self.assertAlmostEqual(rg.eye_height_for_diameter(2.0, 0.35)[0], 0.779558 / 0.35, delta=1e-5)
        self.assertAlmostEqual(rg.eye_height_for_diameter(2.0, 0.5)[0], 1.559116, delta=1e-5)
        self.assertAlmostEqual(rg.eye_height_for_diameter(2.0, 0.2)[0], 3.89779, delta=1e-4)

    def test_waist_is_level_with_the_ball_top_for_any_kappa_and_size(self):
        for diameter in DIAMETERS:
            for kappa in KAPPAS:
                ride = rg.ride_for(diameter, kappa)
                self.assertFalse(ride.clamped)
                # Hips are kappa * E below the eye: one diameter above the ball's lowest point.
                self.assertAlmostEqual(ride.waist_above_ball_bottom_m, diameter,
                                       delta=diameter * 1e-9)
                self.assertAlmostEqual(ride.hips_below_eye_m, kappa * ride.eye_height_m, delta=1e-12)

    def test_eye_height_is_linear_in_the_diameter_and_inverse_in_kappa(self):
        for kappa in KAPPAS:
            unit = rg.eye_height_for_diameter(1.0, kappa)[0]
            for diameter in DIAMETERS:
                self.assertAlmostEqual(rg.eye_height_for_diameter(diameter, kappa)[0],
                                       unit * diameter, delta=1e-9 * diameter)
        self.assertAlmostEqual(rg.eye_height_for_diameter(2.0, 0.2)[0]
                               / rg.eye_height_for_diameter(2.0, 0.5)[0], 2.5, delta=1e-9)

    def test_platform_range(self):
        # kappa 0.35: 0.779558 * 0.025 / 0.35 = 0.0557 m, under the 0.1 m floor; a 200 m ball
        # would need 222.7 m, over the 100 m ceiling; the 20 m cap needs 22.3 m.
        eye, clamped = rg.eye_height_for_diameter(0.05, 0.35)
        self.assertEqual(eye, rg.MIN_EYE_HEIGHT_M)
        self.assertTrue(clamped)
        eye, clamped = rg.eye_height_for_diameter(200.0, 0.35)
        self.assertEqual(eye, rg.MAX_EYE_HEIGHT_M)
        self.assertTrue(clamped)
        self.assertFalse(rg.eye_height_for_diameter(20.0, 0.35)[1])

    def test_a_clamped_size_moves_the_waist_off_the_top(self):
        # Rider bigger than the rule: hips are kappa * 0.1 below the eye instead of 0.0195 m.
        ride = rg.ride_for(0.05, 0.35)
        self.assertTrue(ride.clamped)
        self.assertAlmostEqual(ride.hips_below_eye_m, 0.035, delta=1e-12)
        self.assertLess(ride.waist_above_ball_bottom_m, 0.05)

    def test_bad_inputs(self):
        with self.assertRaises(ValueError):
            rg.eye_offset(-1.0)
        with self.assertRaises(ValueError):
            rg.eye_height_for_diameter(0.0, 0.35)
        with self.assertRaises(ValueError):
            rg.eye_height_for_diameter(float("nan"), 0.35)
        with self.assertRaises(ValueError):
            rg.eye_height_for_diameter(2.0, 0.0)
        with self.assertRaises(ValueError):
            rg.eye_height_for_diameter(2.0, -0.2)


class RateLimitTests(unittest.TestCase):
    def test_smoothing_is_the_usual_low_pass(self):
        # alpha = 1 - exp(-2 pi 5 0.02) = 0.46651
        self.assertAlmostEqual(rg.smooth(1.0, 2.0, 0.02, 5.0), 1.4665, delta=1e-4)
        self.assertEqual(rg.smooth(1.0, 2.0, 0.02, 0.0), 1.0)
        self.assertEqual(rg.smooth(1.0, 2.0, 0.0, 5.0), 1.0)

    def test_half_percent_step(self):
        self.assertTrue(rg.should_apply(0.0, 1.0, 0.005))
        self.assertFalse(rg.should_apply(10.0, 10.04, 0.005))
        self.assertTrue(rg.should_apply(10.0, 10.06, 0.005))

    def test_the_interval_is_part_of_the_rate_limit(self):
        self.assertFalse(rg.should_send(10.0, 11.0, 0.005, 0.05, 0.1))
        self.assertTrue(rg.should_send(10.0, 11.0, 0.005, 0.1, 0.1))
        self.assertFalse(rg.should_send(10.0, 10.01, 0.005, 5.0, 0.1))

    def test_a_ball_jump_is_followed_with_at_most_ten_sends_a_second(self):
        dt, sent, smooth, last, sends, prev = 1.0 / 60.0, 1.0, 1.0, -10.0, 0, 1.0
        for frame in range(600):
            t = frame * dt
            smooth = rg.smooth(smooth, 2.0, dt, 5.0)
            if rg.should_send(sent, smooth, 0.005, t - last, 0.1):
                sent, last, sends = smooth, t, sends + 1
                self.assertGreaterEqual(sent, prev)
                self.assertLessEqual(sent, 2.0)
                prev = sent
        self.assertLessEqual(sends, 101)
        self.assertAlmostEqual(sent, 2.0, delta=2.0 * 0.005 * 1.01)

    def test_whole_growth_is_about_nine_hundred_requests(self):
        # E is linear in D: ln(20 / 0.222) / ln(1.005) = 4.5008 / 0.0049875 = 902.
        steps = math.log(rg.eye_height_for_diameter(20.0, 0.4)[0]
                         / rg.eye_height_for_diameter(0.222, 0.4)[0]) / math.log(1.005)
        self.assertAlmostEqual(steps, 902.4, delta=1.0)


if __name__ == "__main__":
    unittest.main()
