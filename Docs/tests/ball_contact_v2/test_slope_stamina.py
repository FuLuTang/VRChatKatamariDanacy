"""Slope stamina golden values and movement mirror SlopeStaminaTests.cs."""
import math
import re
import unittest
from pathlib import Path

from Docs.Design.BallMotor.contact_v2 import contact_v2 as m
from Docs.tests.ball_contact_v2.test_contact_v2 import slope_world


class SlopeStaminaTests(unittest.TestCase):
    def test_boundaries(self):
        self.assertFalse(m.is_slope(45.5))
        self.assertTrue(m.is_slope(50))
        self.assertTrue(m.is_slope(60))
        self.assertFalse(m.is_slope(60.01))

    def test_shared_golden_drain_and_size_dash_time_effects(self):
        baseline = m.slope_drain_rate(50, .3, 0, 2)
        self.assertAlmostEqual(17.117117, baseline, places=5)
        self.assertAlmostEqual(22.834234, m.slope_drain_rate(58, .3, 1, 2), places=5)
        self.assertGreater(m.slope_drain_rate(58, .3, 0, 2), baseline)
        self.assertGreater(m.slope_drain_rate(50, .3, 3, 2), baseline)
        self.assertLess(m.slope_drain_rate(50, 1.2, 0, 2), baseline)
        self.assertAlmostEqual(baseline * .625, m.slope_drain_rate(50, .3, 0, 3.2))

    def test_slope_vertical_speed_is_the_slow_climb_rate(self):
        v = m.slope_uphill_vertical_speed(1, 2, 50, .3, 0)
        self.assertTrue(.2 <= v <= .3)
        self.assertAlmostEqual(.27488, v, places=6)   # golden shared with C#: 0.917 * D
        self.assertAlmostEqual(v / 1.6, m.slope_uphill_vertical_speed(1, 2, 50, .3, 4), places=6)
        self.assertAlmostEqual(v * .35, m.slope_uphill_vertical_speed(1, 2, 50, .3, 30), places=6)

    def test_slope_vertical_speed_does_not_grow_with_steepness(self):
        ref = m.slope_uphill_vertical_speed(1, 2, 46, .3, 0)
        for theta in range(46, 61, 2):
            self.assertAlmostEqual(ref, m.slope_uphill_vertical_speed(1, 2, theta, .3, 0),
                                   places=9, msg=theta)
        # the wall formula keeps its angle factor
        wall90 = m.climb_speed(1, 2, 90, 0, .45)
        self.assertAlmostEqual(wall90, ref, places=9)
        self.assertAlmostEqual(wall90 * m.climb_shape(50) / m.climb_shape(90),
                               m.climb_speed(1, 2, 50, 0, .45), places=9)
        self.assertGreater(m.climb_speed(1, 2, 50, 0, .45), 4 * wall90)

    def test_slope_vertical_speed_scales_with_diameter_push_and_dash_cap(self):
        base = m.slope_uphill_vertical_speed(1, 2, 50, .3, 0)
        self.assertAlmostEqual(2 * base, m.slope_uphill_vertical_speed(1, 2, 50, .6, 0), places=9)
        self.assertAlmostEqual(4 * base, m.slope_uphill_vertical_speed(1, 2, 50, 1.2, 0), places=9)
        self.assertAlmostEqual(.5 * base, m.slope_uphill_vertical_speed(.5, 2, 50, .3, 0), places=9)
        self.assertAlmostEqual(1.5 * base, m.slope_uphill_vertical_speed(1, 3.2, 50, .3, 0), places=9)
        self.assertLessEqual(m.slope_uphill_vertical_speed(1, 50, 50, .3, 0), 1.5 * base + 1e-9)

    def test_full_push_tops_the_c06_board_in_the_slow_climb_window(self):
        # D=0.30, 1.5 m run-up (the C06 cell); C# twin: SlopeStaminaFullPushTopsTheFifty...
        world = slope_world(50, .6, 4)
        ball = m.State((0., .15, 5.5), .15)
        top = None
        min_stamina = 100.
        for i in range(400):
            m.step(world, ball, (0., 0., 1.), 4., .02, drive_accel=7.5)
            min_stamina = min(min_stamina, ball.slope_stamina)
            if ball.center[1] - .15 >= .58:
                top = (i + 1) * .02
                break
        self.assertIsNotNone(top)
        self.assertGreaterEqual(top, 2.5)   # slow climb, not the 0.77 s dash-speed bug
        self.assertLessEqual(top, 4.0)
        self.assertGreater(min_stamina, 30)

    def test_tunings_match_csharp(self):
        root = Path(__file__).resolve().parents[3]
        source = (root / 'VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime/BallContact/ContactTuning.cs').read_text()
        # Every new slope constant has a single C# owner and an exact Python twin.
        for cs, py in [('ThetaSlopeMaxDeg', 'THETA_SLOPE_MAX_DEG'),
                       ('SlopeRefillMaxDeg', 'SLOPE_REFILL_MAX_DEG'),
                       ('SlopeStaminaMax', 'SLOPE_STAMINA_MAX'),
                       ('SlopeDrainSaturationDeg', 'SLOPE_DRAIN_SATURATION_DEG'),
                       ('SlopeDrainPerSecond', 'SLOPE_DRAIN_PER_SECOND'),
                       ('SlopeReferenceDiameterM', 'SLOPE_REFERENCE_DIAMETER_M'),
                       ('SlopeFatiguePerSecond', 'SLOPE_FATIGUE_PER_SECOND'),
                       ('SlopeMinimumSpeedFactor', 'SLOPE_MINIMUM_SPEED_FACTOR'),
                       ('SlopeRestSeconds', 'SLOPE_REST_SECONDS'),
                       ('SlopeRestSpeedMps', 'SLOPE_REST_SPEED_MPS')]:
            value = re.search(r'const float ' + cs + r' = ([\d.]+)f;', source)
            self.assertIsNotNone(value, cs)
            self.assertEqual(float(value.group(1)), getattr(m, py), cs)

    def test_exhaustion_uses_same_rest_delay_then_slides(self):
        for theta in [50, 58]:
            state = (0., 5., 0., False, 0.)
            for _ in range(14):
                state = m.slope_motion(theta, .3, 1, 2, .02, *state)
            self.assertFalse(state[3])
            for _ in range(2):
                state = m.slope_motion(theta, .3, 1, 2, .02, *state)
            self.assertTrue(state[3])
            self.assertLess(state[4], 0)
            self.assertEqual(state[0], 0)

    def test_substep_partition_does_not_change_stamina(self):
        def run(dt):
            state = (100., 0., 0., False, 0.)
            for _ in range(round(2 / dt)):
                state = m.slope_motion(58, .3, 1, 2, dt, *state)
            return state[0]
        self.assertAlmostEqual(run(.02), run(.005), places=9)

    def test_point_six_metre_board_is_not_height_capped(self):
        world = slope_world(50, .6, 4)
        ball = m.State((0., .15, 6.), .15)
        peak = .15
        for _ in range(200):
            m.step(world, ball, (0., 0., 1.), 4., .02, drive_accel=7.5)
            peak = max(peak, ball.center[1])
        self.assertGreater(peak - .15, .59)
        self.assertGreater(ball.center[2], 7 + .6 / math.tan(math.radians(50)))

    def test_long_slope_exhausts_and_slides_back(self):
        for theta in [50, 58]:
            world = slope_world(theta, 20, 4)
            ball = m.State((0., .15, 6.7), .15)
            exhausted = slid = returned = False
            peak = .15
            for _ in range(1500):
                m.step(world, ball, (0., 0., 1.), 4., .02, drive_accel=7.5)
                exhausted |= ball.slope_stamina == 0
                slid |= ball.slope_sliding
                peak = max(peak, ball.center[1])
                if exhausted and slid and ball.center[1] < .17:
                    returned = True
                    break
            self.assertTrue(exhausted)
            self.assertTrue(returned)
            self.assertGreater(peak - .15, .45)

    def test_ride_speed_and_stamina_are_unchanged(self):
        for theta in [25, 45]:
            a = math.radians(theta)
            normal = (0., math.cos(a), -math.sin(a))
            center = (0., 1 + .15 * normal[1], 7 + 1 / math.tan(a) + .15 * normal[2])
            ball = m.State(center, .15, v_planar=(0., 0., 4.),
                           slope_stamina=7, slope_uphill_seconds=4)
            for _ in range(20):
                m.step(slope_world(theta, 20, 4), ball, (0., 0., 1.), 4, .02)
            self.assertEqual(ball.v_planar[2], 4)
            self.assertEqual(ball.slope_stamina, 7)
            self.assertFalse(ball.climb_active)

    def test_entering_a_wall_starts_its_budget_at_the_slope_exit(self):
        wall = m.Obb((0., 5., 2.5), (1., 0., 0.), (0., 1., 0.), (0., 0., 1.),
                     (2., 5., .5), 1)
        a = math.radians(50)
        ball = m.State((0., 3., 1.85), .15, climb_active=True, climb_anchor_y=.15,
                       climb_surface_id=2, climb_normal=(0., math.cos(a), -math.sin(a)),
                       slope_stamina=17)
        m.step(m.World(obbs=[wall]), ball, (0., 0., 1.), 4, .02)
        self.assertTrue(ball.climb_active)
        self.assertAlmostEqual(ball.climb_anchor_y, 3)
        self.assertEqual(ball.slope_stamina, 17)
        self.assertFalse(ball.climb_locked)
        ball.climb_active = False
        ball.climb_normal = (0., math.cos(a), -math.sin(a))
        ball.climb_anchor_y = .15
        ball.climb_along_speed = 0
        ball.v_y = 0
        ball.v_planar = (0., 0., 0.)
        ball.center = (0., 3., 1.85)
        m.step(m.World(obbs=[wall]), ball, (0., 0., 1.), 4, .02)
        self.assertAlmostEqual(ball.climb_anchor_y, 3)

    def test_level_refills(self):
        ball = m.State((0., .15, 0.), .15, slope_stamina=7, slope_uphill_seconds=4)
        m.step(m.World(ground=m.Plane((0., 1., 0.))), ball, (0., 0., 1.), 0., .02)
        self.assertEqual(ball.slope_stamina, 100)
        self.assertEqual(ball.slope_uphill_seconds, 0)
