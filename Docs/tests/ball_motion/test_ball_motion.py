import math
import unittest

from Docs.Design.BallMotion.ball_motion import (
    BallSimulator, MotionConfig, Plane, radius_from_growth,
)


class BallMotionTests(unittest.TestCase):
    def test_fast_sweep_does_not_tunnel_thin_wall(self):
        sim = BallSimulator(MotionConfig(fixed_dt_s=0.1, base_radius_m=0.1, max_radius_m=0.1,
                                         friction_per_s=0, braking_mps2=0))
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
        self.assertTrue(0 <= sim.config.max_speed_mps*(1-sim.config.growth_speed_factor) <= sim.config.max_speed_mps)
        self.assertTrue(0 <= sim.config.turn_rate_degps*(1-sim.config.growth_turn_factor) <= sim.config.turn_rate_degps)

    def test_out_of_bounds_recovers_to_origin(self):
        sim = BallSimulator(MotionConfig(bounds_half_extent_m=1))
        sim.state.x = 2
        sim.advance(sim.config.fixed_dt_s)
        self.assertTrue(sim.state.out_of_bounds)
        self.assertEqual((sim.state.x, sim.state.z), (0.0, 0.0))


if __name__ == "__main__":
    unittest.main()
