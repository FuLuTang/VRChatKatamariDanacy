import math
import unittest

from Docs.Design.BallMotion.ball_motion import (
    BallSimulator, MotionConfig, Plane, drive_speed_fraction, radius_from_growth,
)


class BallMotionTests(unittest.TestCase):
    def test_fast_sweep_does_not_tunnel_thin_wall(self):
        sim = BallSimulator(MotionConfig(fixed_dt_s=0.1, base_radius_m=0.1, max_radius_m=0.1,
                                         friction_per_s=0, braking_mps2=0,
                                         rolling_resistance_mps2=0, lateral_grip_per_s=0,
                                         max_speed_mps=200.0))
        sim.state.x = 2.0
        sim.state.y = 0.1
        sim.state.vx = -100.0
        wall = Plane(1, 0, 0, offset_m=0)
        sim.advance(0.1, walls=(wall,))
        self.assertGreaterEqual(sim.state.x, 0.1-1e-9)
        self.assertGreaterEqual(sim.state.vx, -1e-9)

    def test_slopes_5_to_15_degrees_stay_supported_and_stable(self):
        for slope in (5, 10, 15):
            sim = BallSimulator()
            for _ in range(240):
                sim.advance(1/120, forward=0.6, slope_deg=slope)
            expected_floor = math.tan(math.radians(slope))*sim.state.x
            self.assertGreaterEqual(sim.state.y+1e-9, expected_floor+sim.state.radius_m)
            self.assertEqual(sim.state.vy, 0.0)
            self.assertEqual(sim.state.stuck_seconds, 0.0)
            self.assertTrue(math.isfinite(sim.state.y))

    def test_delay_and_fixed_step_are_bounded_and_reproducible(self):
        a, b = BallSimulator(), BallSimulator()
        for _ in range(100):
            a.advance(0.025, forward=1, turn=.2)
        b.advance(2.5, forward=1, turn=.2)
        self.assertLess(abs(a.state.x-b.state.x), 1e-9)
        self.assertLess(abs(a.state.z-b.state.z), 1e-9)
        self.assertLess(a._accumulator, a.config.fixed_dt_s+1e-9)

    def test_growth_clamps_and_parameters_remain_legal_at_max(self):
        self.assertEqual(radius_from_growth(.5, 2, 2), 2)
        sim = BallSimulator()
        sim.set_growth(1)
        sim.advance(1/120, forward=1, turn=1)
        self.assertEqual(sim.state.radius_m, sim.config.max_radius_m)

    def test_out_of_bounds_recovers_to_origin(self):
        sim = BallSimulator(MotionConfig(bounds_half_extent_m=1))
        sim.state.x = 2
        sim.advance(sim.config.fixed_dt_s)
        self.assertTrue(sim.state.out_of_bounds)
        self.assertEqual((sim.state.x, sim.state.z), (0.0, 0.0))

    # ---- Inertia model (WLK A2 defaults; Docs/Design/BallMotor/BALL_INERTIA.md) ----

    @staticmethod
    def _run(sim, seconds, **inputs):
        speeds = []
        for _ in range(int(round(seconds/sim.config.fixed_dt_s))):
            sim.advance(sim.config.fixed_dt_s, **inputs)
            speeds.append(math.hypot(sim.state.vx, sim.state.vz))
        return speeds

    def test_forward_terminal_speed_is_the_cap_and_ramp_is_monotone(self):
        sim = BallSimulator()
        speeds = self._run(sim, 8.0, forward=1)
        self.assertAlmostEqual(speeds[-1], sim.config.max_speed_mps, places=6)
        self.assertTrue(all(b >= a-1e-12 for a, b in zip(speeds, speeds[1:])))
        self.assertLessEqual(max(speeds), sim.config.max_speed_mps+1e-9)

    def test_forward_intent_uses_the_wlk_stick_thresholds(self):
        for power, fraction in ((0.145, 0.2), (0.29, 0.4), (0.5, 0.4),
                                (0.989, 0.4), (0.99, 1.0), (1.0, 1.0), (-0.5, 0.4)):
            with self.subTest(power=power):
                self.assertAlmostEqual(drive_speed_fraction(power), fraction)
                sim = BallSimulator()
                self._run(sim, 3.0, forward=power)
                self.assertAlmostEqual(sim.state.vz, (4.0 if power > 0 else -2.0)*fraction)
        self.assertEqual(drive_speed_fraction(0.0), 0.0)

    def test_reducing_push_rolls_down_to_target_without_a_jump(self):
        for sign, initial, target in ((1, 4.0, 1.6), (-1, 2.0, 0.8)):
            sim = BallSimulator(MotionConfig(fixed_dt_s=0.02))
            self._run(sim, 1.2, forward=sign)
            self.assertAlmostEqual(sign * sim.state.vz, initial)
            for i, speed in enumerate(self._run(sim, 1.4, forward=sign*0.5), 1):
                self.assertAlmostEqual(speed, max(target, initial - i*0.038))

    def test_half_reverse_brakes_before_using_the_reverse_target(self):
        sim = BallSimulator(MotionConfig(fixed_dt_s=0.02))
        sim.state.vz = 4.0
        for i, speed in enumerate(self._run(sim, 1.0, forward=-0.5), 1):
            self.assertAlmostEqual(speed, 4.0 - i*0.08)
        self._run(sim, 0.6, forward=-0.5)
        self.assertAlmostEqual(sim.state.vz, -0.8)

    def test_release_coasts_monotonically_to_rest_without_reversing(self):
        sim = BallSimulator()
        self._run(sim, 8.0, forward=1)
        speeds = []
        for _ in range(int(4/sim.config.fixed_dt_s)):
            sim.advance(sim.config.fixed_dt_s)
            speeds.append(sim.state.vz)
        self.assertTrue(all(b <= a+1e-12 for a, b in zip(speeds, speeds[1:])))
        self.assertGreaterEqual(min(speeds), 0.0)
        self.assertEqual(speeds[-1], 0.0)
        self.assertAlmostEqual(speeds[0], 4.0 - 1.9*sim.config.fixed_dt_s)
        # A visible glide, not an instant stop: still moving 0.5 s after release.
        self.assertGreater(speeds[int(0.5/sim.config.fixed_dt_s)], 1.0)

    def test_reverse_input_brakes_faster_than_coasting(self):
        coast, brake = BallSimulator(), BallSimulator()
        for sim in (coast, brake):
            self._run(sim, 8.0, forward=1)
        self._run(coast, 0.5)
        self._run(brake, 0.5, forward=-1)
        self.assertLess(brake.state.vz, coast.state.vz)

    def test_reverse_speed_is_capped_at_half_the_forward_cap(self):
        sim = BallSimulator()
        speeds = self._run(sim, 12.0, forward=-1)
        self.assertAlmostEqual(sim.state.vz, -0.5*sim.config.max_speed_mps, places=6)
        self.assertLessEqual(max(speeds), 0.5*sim.config.max_speed_mps+1e-9)

    def test_turn_lags_velocity_direction_behind_heading_and_grip_orders_it(self):
        lags = []
        for grip in (1.2, 2.0, 3.0):
            sim = BallSimulator(MotionConfig(lateral_grip_per_s=grip))
            self._run(sim, 8.0, forward=1)
            while sim.state.heading_deg < 90.0:
                sim.advance(sim.config.fixed_dt_s, forward=1, turn=1)
            vel_deg = math.degrees(math.atan2(sim.state.vx, sim.state.vz)) % 360.0
            lags.append(sim.state.heading_deg - vel_deg)
        self.assertTrue(all(l > 5.0 for l in lags))
        self.assertTrue(lags[0] > lags[1] > lags[2])

    # Diameter ratios s = D / D0 for D0 = 0.222 m and D = 0.222 / 0.8 / 5 m.
    SCALES = (1.0, 0.8/0.222, 5.0/0.222)

    def _scaled(self, s, dt=0.02):
        cfg = MotionConfig(fixed_dt_s=dt, max_radius_m=1000.0)
        sim = BallSimulator(cfg)
        sim.state.radius_m = cfg.base_radius_m * s
        return sim

    def test_start_up_in_diameters_per_second_is_scale_invariant(self):
        results = []
        for s in self.SCALES:
            sim = self._scaled(s)
            cap = sim.config.max_speed_mps * s
            times = {}
            t = 0.0
            while 0.95 not in times and t < 10:
                sim.advance(0.02, forward=1)
                t += 0.02
                v = sim.state.vz
                for q in (0.5, 0.95):
                    if q not in times and v >= q*cap - 1e-9:
                        times[q] = t
            results.append((times[0.5], times[0.95]))
        for r in results:
            self.assertAlmostEqual(r[0], results[0][0], places=6)
            self.assertAlmostEqual(r[1], results[0][1], places=6)
        # 地图 0：7.5 m/s²，0.02 s 步进。
        self.assertAlmostEqual(results[0][1], 0.52, delta=0.05)

    def test_release_glide_in_diameters_is_scale_invariant(self):
        glides = []
        for s in self.SCALES:
            sim = self._scaled(s)
            d = 2.0 * sim.state.radius_m
            self._run(sim, 6.0, forward=1)
            z0 = sim.state.z
            while math.hypot(sim.state.vx, sim.state.vz) > 0.1*s:
                sim.advance(0.02)
            glides.append((sim.state.z - z0) / d)
        for g in glides:
            self.assertAlmostEqual(g, glides[0], places=6)
        # 参考配置 D0 = 1 m；滑行 4.17 m = 4.17 个球径。
        self.assertAlmostEqual(glides[0], 4.17, delta=0.01)

    def test_terminal_speed_scales_with_diameter_and_turn_rate_does_not(self):
        small, big = self._scaled(1.0), self._scaled(5.0)
        for sim in (small, big):
            self._run(sim, 8.0, forward=1)
        self.assertAlmostEqual(big.state.vz, 5.0*small.state.vz, places=6)
        a, b = self._scaled(1.0), self._scaled(5.0)
        a.advance(0.02, turn=1)
        b.advance(0.02, turn=1)
        self.assertAlmostEqual(a.state.heading_deg, b.state.heading_deg, places=9)

    def test_legacy_equivalent_values_stop_in_about_half_a_second(self):
        # Old-behaviour comparison set: braking = rolling resistance = 8, huge grip.
        legacy = MotionConfig(acceleration_mps2=5.0, braking_mps2=8.0, friction_per_s=1.2,
                              rolling_resistance_mps2=8.0, lateral_grip_per_s=1000.0)
        sim = BallSimulator(legacy)
        self._run(sim, 8.0, forward=1)
        coast = self._run(sim, 1.0)
        self.assertEqual(coast[int(0.5/legacy.fixed_dt_s)+5], 0.0)


if __name__ == "__main__":
    unittest.main()
