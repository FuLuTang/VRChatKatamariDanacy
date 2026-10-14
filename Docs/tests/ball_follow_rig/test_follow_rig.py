import inspect
import math
import unittest

from Docs.Design.BallLayers.FollowRig.follow_rig import FollowRig


def _wrap_shortest(target, current):
    return (target - current + 180.0) % 360.0 - 180.0


class FollowRigTests(unittest.TestCase):
    def test_heading_wrap_uses_short_arc_in_both_directions(self):
        forward = FollowRig(height_filter_hz=0.0, yaw_filter_hz=1.0)
        first = forward.update((0.0, 0.0, 0.0), 359.0, 0.01, 1.0, 0.4, 1.8)
        next_frame = forward.update((0.0, 0.0, 0.0), 1.0, 0.05, 1.0, 0.4, 1.8)
        short_step = _wrap_shortest(next_frame.yaw_deg, first.yaw_deg)
        self.assertGreater(short_step, 0.0)
        self.assertLess(short_step, 2.0)

        reverse = FollowRig(height_filter_hz=0.0, yaw_filter_hz=1.0)
        first = reverse.update((0.0, 0.0, 0.0), 1.0, 0.01, 1.0, 0.4, 1.8)
        next_frame = reverse.update((0.0, 0.0, 0.0), 359.0, 0.05, 1.0, 0.4, 1.8)
        short_step = _wrap_shortest(next_frame.yaw_deg, first.yaw_deg)
        self.assertLess(short_step, 0.0)
        self.assertGreater(short_step, -2.0)

    def test_filters_are_bounded_and_do_not_overshoot(self):
        rig = FollowRig(height_filter_hz=2.0, yaw_filter_hz=3.0)
        rig.update((0.0, 0.0, 0.0), 0.0, 0.1, 1.0, 0.4, 1.8)
        frame = rig.update((10.0, 2.0, -6.0), 90.0, 0.1, 1.7, 0.4, 1.8)
        self.assertGreater(frame.position[0], 0.0)
        self.assertLess(frame.position[0], 10.0)
        self.assertGreater(frame.position[2], -6.0)
        self.assertLess(frame.position[2], 0.0)
        self.assertGreater(frame.yaw_deg, 0.0)
        self.assertLess(frame.yaw_deg, 90.0)
        self.assertGreaterEqual(frame.position[1] - 2.0, 0.4)
        self.assertLessEqual(frame.position[1] - 2.0, 1.8)
        for _ in range(300):
            frame = rig.update((10.0, 2.0, -6.0), 90.0, 1.0 / 60.0,
                               1.7, 0.4, 1.8)
        self.assertAlmostEqual(frame.position[0], 10.0, delta=1e-5)
        self.assertAlmostEqual(frame.position[2], -6.0, delta=1e-5)
        self.assertAlmostEqual(frame.position[1] - 2.0, 1.7, delta=1e-5)
        self.assertAlmostEqual(frame.yaw_deg, 90.0, delta=1e-5)

    def test_respawn_or_teleport_marker_snaps_and_restarts_filter_baseline(self):
        rig = FollowRig(height_filter_hz=0.5, yaw_filter_hz=0.5)
        rig.update((2.0, 1.0, 3.0), 15.0, 0.1, 1.0, 0.5, 1.5)
        lagged = rig.update((20.0, 4.0, -10.0), 30.0, 0.01, 1.0, 0.5, 1.5)
        self.assertLess(lagged.position[0], 20.0)
        self.assertLess(abs(_wrap_shortest(30.0, lagged.yaw_deg)), 30.0)
        respawned = rig.update((-90.0, -4.0, 120.0), 271.0, 0.0,
                               8.0, 0.6, 1.4, reset=True)
        self.assertEqual(respawned.position, (-90.0, -2.6, 120.0))
        self.assertEqual(respawned.yaw_deg, 271.0)
        self.assertEqual(rig.frame, respawned)

    def test_eye_height_is_clamped_to_explicit_finite_limits(self):
        rig = FollowRig(height_filter_hz=100.0, yaw_filter_hz=0.0)
        low = rig.update((0.0, 5.0, 0.0), 0.0, 0.2, -100.0, 0.75, 1.25)
        self.assertAlmostEqual(low.position[1] - 5.0, 0.75)
        high = rig.update((0.0, 5.0, 0.0), 0.0, 0.2, 100.0, 0.75, 1.25)
        self.assertAlmostEqual(high.position[1] - 5.0, 1.25)
        inside = rig.update((0.0, 5.0, 0.0), 0.0, 0.2, 1.0, 0.75, 1.25)
        self.assertAlmostEqual(inside.position[1] - 5.0, 1.0)

    def test_invalid_inputs_are_rejected_atomically(self):
        rig = FollowRig()
        old = rig.update((1.0, 2.0, 3.0), 45.0, 0.1, 1.0, 0.5, 1.5)
        bad_calls = (
            ((float("nan"), 0.0, 0.0), 0.0, 0.1, 1.0, 0.5, 1.5),
            ((0.0, 0.0, 0.0), float("inf"), 0.1, 1.0, 0.5, 1.5),
            ((0.0, 0.0, 0.0), 0.0, -0.1, 1.0, 0.5, 1.5),
            ((0.0, 0.0, 0.0), 0.0, 0.1, float("nan"), 0.5, 1.5),
            ((0.0, 0.0, 0.0), 0.0, 0.1, 1.0, 2.0, 1.0),
        )
        for args in bad_calls:
            with self.subTest(args=args), self.assertRaises(ValueError):
                rig.update(*args)
            self.assertEqual(rig.frame, old)
        with self.assertRaises(ValueError):
            rig.update((0.0, 0.0, 0.0), 0.0, 0.1, 1.0, 0.5, 1.5, reset=1)
        self.assertEqual(rig.frame, old)

    def test_huge_filter_product_is_rejected_without_partial_commit(self):
        rig = FollowRig(height_filter_hz=1e308, yaw_filter_hz=1.0)
        old = rig.update((0.0, 0.0, 0.0), 0.0, 0.0, 1.0, 0.5, 1.5)
        with self.assertRaises(OverflowError):
            rig.update((10.0, 0.0, 0.0), 90.0, 1e308, 1.0, 0.5, 1.5)
        self.assertEqual(rig.frame, old)

    def test_interface_has_only_scalar_horizontal_heading_not_hmd_or_spin(self):
        parameters = tuple(inspect.signature(FollowRig.update).parameters)
        self.assertEqual(parameters, ("self", "ball_position", "horizontal_yaw_deg", "dt_s",
                                      "eye_height_m", "eye_height_min_m", "eye_height_max_m", "reset"))
        frame = FollowRig().update((0.0, 1.0, 0.0), 123.0, 0.0, 1.0, 0.5, 1.5)
        self.assertFalse(hasattr(frame, "roll_deg"))
        self.assertFalse(hasattr(frame, "pitch_deg"))

    def test_deterministic_long_sequence_remains_finite_and_height_limited(self):
        rig = FollowRig(height_filter_hz=4.0, yaw_filter_hz=6.0)
        for i in range(1000):
            x = math.sin(i * 0.013) * 50.0
            z = math.cos(i * 0.017) * 30.0
            yaw = (359.0 + i * 0.7) % 360.0
            frame = rig.update((x, float(i % 9) - 4.0, z), yaw, 1.0 / 90.0,
                               2.0, 0.7, 1.4)
            self.assertTrue(all(math.isfinite(value) for value in frame.position))
            self.assertTrue(math.isfinite(frame.yaw_deg))
            self.assertGreaterEqual(frame.position[1] - (float(i % 9) - 4.0), 0.7 - 1e-12)
            self.assertLessEqual(frame.position[1] - (float(i % 9) - 4.0), 1.4 + 1e-12)


if __name__ == "__main__":
    unittest.main()
