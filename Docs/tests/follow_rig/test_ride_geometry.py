"""Tests for the ride geometry (Docs/Design/BallLayers/FollowRig/ride_geometry.py).

Expectation values are hand-derived in the comments (closed forms), never copied from the module
under test. The C# twins (RideViewMath, RideScaleMath; Tests/EditMode/Riding) pin the same numbers.
"""
import math
import unittest

import Docs.Design.BallLayers.FollowRig.ride_geometry as rg

S45 = math.sqrt(0.5)
# e = (1 + sin 45) / (1 - 0.125 - 0.328 sin 45) = 1.707107 / 0.643069
E45 = (1 + S45) / (1 - 0.125 - 0.328 * S45)
DIAMETERS = (0.222, 0.3, 0.5, 2.0, 20.0)


class StanceTests(unittest.TestCase):
    def test_closed_form_at_45(self):
        self.assertAlmostEqual(rg.push_reach(), 0.328, delta=1e-12)
        self.assertAlmostEqual(rg.eye_height_per_radius(), 2.654625, delta=1e-5)
        self.assertAlmostEqual(rg.eye_up_per_radius(), 1.654625, delta=1e-5)
        # back = -0.055 e + (1 + 0.328 e) cos 45
        self.assertAlmostEqual(rg.eye_back_per_radius(), -0.055 * E45 + (1 + 0.328 * E45) * S45,
                               delta=1e-12)
        self.assertAlmostEqual(rg.eye_back_per_radius(), 1.176792, delta=1e-5)

    def test_feet_on_ground_and_arm_through_the_centre(self):
        for psi in rg.CANDIDATE_ANGLES_DEG:
            st = rg.stance(psi)
            # Eye one eye height above the ground: feet on the ground.
            self.assertAlmostEqual(st.eye[1], st.e, delta=1e-12)
            self.assertEqual(st.feet[1], 0.0)
            # The shoulder-to-hand segment is the push reach and points at the centre.
            sx, sy = st.shoulder
            hx, hy = st.hand
            self.assertAlmostEqual(math.hypot(hx - sx, hy - sy), rg.push_reach() * st.e, delta=1e-9)
            self.assertAlmostEqual(math.degrees(math.atan2(sy - hy, hx - sx)), psi, delta=1e-9)
            self.assertAlmostEqual(math.degrees(math.atan2(hy - 1.0, -hx)), psi, delta=1e-9)
            # The hand is on the surface.
            self.assertAlmostEqual(math.hypot(hx, hy - 1.0), 1.0, delta=1e-12)

    def test_view_at_45(self):
        st = rg.stance()
        # atan2(1.654625, 1.176792) = 54.58 deg; asin(1 / 2.0305) = 29.51 deg.
        self.assertAlmostEqual(st.centre_depression_deg, 54.58, delta=0.01)
        self.assertAlmostEqual(st.half_angle_deg, 29.51, delta=0.01)
        self.assertAlmostEqual(st.top_edge_deg, 25.07, delta=0.01)
        # Ball top 0.654625 r below the eye = 0.2466 E (the chest), hands 0.177 E forward and
        # 0.357 E down from the eye.
        self.assertAlmostEqual(st.top_below_eye_per_e, 0.654625 / 2.654625, delta=1e-5)
        f, d = st.hand_from_eye_per_e
        self.assertAlmostEqual(f, (1.176792 - S45) / 2.654625, delta=1e-5)
        self.assertAlmostEqual(d, (1.654625 - S45) / 2.654625, delta=1e-5)
        # D / E = 2 / 2.654625 = 0.753.
        self.assertAlmostEqual(st.diameter_per_e, 0.7534, delta=1e-4)

    def test_hands_reach_across_real_bodies(self):
        # Straight reach 0.38-0.45 E: the push uses at most 0.328 / 0.38 = 86 % of it.
        for arm in (0.38, 0.41, 0.45):
            self.assertLess(rg.hand_reach_needed_per_e(rg.stance(), arm), 0.87)

    def test_steeper_push_shows_more_ground(self):
        rows = [rg.stance(p) for p in rg.CANDIDATE_ANGLES_DEG]
        for a, b in zip(rows, rows[1:]):
            self.assertLess(b.ground_visible_ahead_of_centre, a.ground_visible_ahead_of_centre)
            self.assertGreater(b.top_edge_deg, a.top_edge_deg)


class SizeTests(unittest.TestCase):
    def test_eye_height_is_linear_and_divided_by_eta(self):
        for d in DIAMETERS:
            e, clamped = rg.eye_height_for_diameter(d)
            self.assertFalse(clamped)
            self.assertAlmostEqual(e, E45 * d / 2, delta=1e-9)
            e9, _ = rg.eye_height_for_diameter(d, 0.9)
            self.assertAlmostEqual(e9, e / 0.9, delta=1e-9)

    def test_hand_values(self):
        # D = 0.3: E = 2.654625 * 0.15 = 0.398194; panel at 1.6 * 0.398194 / 1.64 = 0.388 m.
        e, _ = rg.eye_height_for_diameter(0.3)
        self.assertAlmostEqual(e, 0.398194, delta=1e-5)
        self.assertAlmostEqual(rg.PANEL_DISTANCE_M * rg.panel_scale(e), 0.38848, delta=1e-4)

    def test_clamp(self):
        self.assertEqual(rg.eye_height_for_diameter(0.05), (0.1, True))
        self.assertEqual(rg.eye_height_for_diameter(200.0), (100.0, True))
        with self.assertRaises(ValueError):
            rg.eye_height_for_diameter(0.0)
        with self.assertRaises(ValueError):
            rg.eye_height_for_diameter(1.0, 0.0)

    def test_panel_scale(self):
        self.assertEqual(rg.panel_scale(1.64), 1.0)
        self.assertEqual(rg.panel_scale(0.0), 1.0)
        self.assertEqual(rg.panel_scale(float("nan")), 1.0)

    def test_panels_stay_off_the_ball(self):
        # Every panel point stays at least 9 deg off the ball's silhouette at pitch 0 (the HUD,
        # low on the left beside the ball, is the closest).
        for panel in rg.PANELS:
            self.assertGreater(rg.panel_clearance_deg(panel), 9.0, panel[0])

    def test_panel_clearance_detects_overlap(self):
        # A panel centred on the ball centre overlaps it.
        st = rg.stance()
        on_ball = ("x", 0.0, -st.centre_depression_deg, 100.0, 100.0, 0.0014)
        self.assertLess(rg.panel_clearance_deg(on_ball), 0.0)

    def test_reach_table(self):
        # D = 2 (r = 1): E = 2.654625; contact (1.176792 - cos45, 1.654625 - sin45) from the eye;
        # shoulder to contact = 0.328 E.
        row = [x for x in rg.reach_table() if x[0] == 2.0][0]
        self.assertAlmostEqual(row[1], E45, delta=1e-9)
        self.assertAlmostEqual(row[4], 1.176792 - S45, delta=1e-5)
        self.assertAlmostEqual(row[5], 1.654625 - S45, delta=1e-5)
        self.assertAlmostEqual(row[7], 0.328 * E45, delta=1e-9)


class FilterTests(unittest.TestCase):
    def test_smooth_and_limits(self):
        self.assertEqual(rg.smooth(1.0, 2.0, 0.0, 5.0), 1.0)
        alpha = 1 - math.exp(-2 * math.pi * 5 * 0.02)
        self.assertAlmostEqual(rg.smooth(1.0, 2.0, 0.02, 5.0), 1.0 + alpha, delta=1e-12)
        self.assertTrue(rg.should_apply(0.0, 1.0, 0.005))
        self.assertFalse(rg.should_apply(1.0, 1.004, 0.005))
        self.assertTrue(rg.should_apply(1.0, 1.006, 0.005))
        self.assertFalse(rg.should_send(1.0, 2.0, 0.005, 0.05, 0.1))
        self.assertTrue(rg.should_send(1.0, 2.0, 0.005, 0.1, 0.1))


class FigureTests(unittest.TestCase):
    def test_svg_is_well_formed(self):
        import xml.dom.minidom
        xml.dom.minidom.parseString(rg.side_view_svg())


if __name__ == "__main__":
    unittest.main()
