import inspect
import math
import unittest

from Docs.Design.BallLayers.FollowRig.follow_rig import FollowRig, try_update


def _wrap_shortest(target, current):
    return (target - current + 180.0) % 360.0 - 180.0


# The motor's committed FollowRigMath inputs (KatamariBallMotor.cs:137-143), written as
# literals on purpose: a changed default must fail a test instead of silently moving the
# expectation. Order is the C# argument order:
# eye_base_up_m, eye_up_per_radius, eye_up_min_m, eye_up_max_m,
# eye_base_back_m, eye_back_per_radius, eye_max_back_m.
MOTOR_EYE = (0.55, 2.4, 0.1, 8.0, 0.75, 2.2, 8.0)


def _step(rig, ball, radius, yaw, dt, *, reset=False, eye=MOTOR_EYE):
    return rig.update(ball, radius, yaw, dt, *eye, reset=reset)


class FollowRigTests(unittest.TestCase):
    def test_heading_wrap_uses_short_arc_in_both_directions(self):
        # 359 -> 1 is a +2 deg arc, not a -358 deg one. The first sample snaps, so dt
        # only matters for the second: alpha = 1 - exp(-2*pi*1*0.05) = 0.269597, giving
        # a 2 * 0.269597 = 0.5392 deg step.
        forward = FollowRig(height_filter_hz=0.0, yaw_filter_hz=1.0)
        first = _step(forward, (0.0, 0.0, 0.0), 0.15, 359.0, 0.01)
        next_frame = _step(forward, (0.0, 0.0, 0.0), 0.15, 1.0, 0.05)
        short_step = _wrap_shortest(next_frame.yaw_deg, first.yaw_deg)
        self.assertGreater(short_step, 0.0)
        self.assertLess(short_step, 2.0)
        self.assertAlmostEqual(short_step, 0.5392, delta=1e-3)

        reverse = FollowRig(height_filter_hz=0.0, yaw_filter_hz=1.0)
        first = _step(reverse, (0.0, 0.0, 0.0), 0.15, 1.0, 0.01)
        next_frame = _step(reverse, (0.0, 0.0, 0.0), 0.15, 359.0, 0.05)
        short_step = _wrap_shortest(next_frame.yaw_deg, first.yaw_deg)
        self.assertLess(short_step, 0.0)
        self.assertGreater(short_step, -2.0)
        self.assertAlmostEqual(short_step, -0.5392, delta=1e-3)

    def test_filters_are_bounded_and_do_not_overshoot(self):
        rig = FollowRig(height_filter_hz=2.0, yaw_filter_hz=3.0)
        # First sample snaps: 0.15 m radius, so 0.75 + 2.2 * 0.15 = 1.08 m back and
        # 0.55 + 2.4 * 0.15 = 0.91 m above the centre; yaw 0 puts the offset on -z.
        first = _step(rig, (0.0, 0.0, 0.0), 0.15, 0.0, 0.1)
        self.assertAlmostEqual(first.position[0], 0.0, delta=1e-12)
        self.assertAlmostEqual(first.position[1], 0.91, delta=1e-12)
        self.assertAlmostEqual(first.position[2], -1.08, delta=1e-12)

        frame = _step(rig, (10.0, 2.0, -6.0), 0.15, 90.0, 0.1)
        self.assertGreater(frame.position[0], 0.0)
        self.assertLess(frame.position[0], 10.0)
        self.assertLess(frame.position[2], -6.0)
        self.assertLess(frame.position[2], 0.0)
        self.assertGreater(frame.yaw_deg, 0.0)
        self.assertLess(frame.yaw_deg, 90.0)
        self.assertGreaterEqual(frame.position[1] - 2.0, MOTOR_EYE[2])
        self.assertLessEqual(frame.position[1] - 2.0, MOTOR_EYE[3])
        # Height stays constant. X/Z use the current centre and the filtered yaw
        # (76.3348°), without a second world-position filter.
        self.assertAlmostEqual(frame.yaw_deg, 76.3348, delta=1e-4)
        self.assertAlmostEqual(frame.position[0], 8.95057, delta=1e-4)
        self.assertAlmostEqual(frame.position[2], -6.25515, delta=1e-4)

        for _ in range(300):
            frame = _step(rig, (10.0, 2.0, -6.0), 0.15, 90.0, 1.0 / 60.0)
        # yaw = 90 puts the back offset on -x: x = 10 - 1.08, z = -6.
        self.assertAlmostEqual(frame.position[0], 10.0 - 1.08, delta=1e-9)
        self.assertAlmostEqual(frame.position[2], -6.0, delta=1e-9)
        self.assertAlmostEqual(frame.position[1] - 2.0, 0.91, delta=1e-9)
        self.assertAlmostEqual(frame.yaw_deg, 90.0, delta=1e-9)

    def test_respawn_or_teleport_marker_snaps_and_restarts_filter_baseline(self):
        rig = FollowRig(height_filter_hz=0.5, yaw_filter_hz=0.5)
        _step(rig, (2.0, 1.0, 3.0), 0.15, 15.0, 0.1)
        lagged = _step(rig, (20.0, 4.0, -10.0), 0.15, 30.0, 0.01)
        self.assertLess(lagged.position[0], 20.0)
        self.assertLess(abs(_wrap_shortest(30.0, lagged.yaw_deg)), 30.0)

        # The 1.4 m rail bounds the CONSTANT part: base 2.0 -> 1.4, so the height is
        # 1.4 + 2.4 * 0.5 = 2.6 m, and 0.75 + 2.2 * 0.5 = 1.85 m of back offset at yaw
        # 270 (sin = -1, cos = 0) lands entirely on +x: x = -90 + 1.85, z = 120, y = -4 + 2.6.
        respawned = _step(rig, (-90.0, -4.0, 120.0), 0.5, 270.0, 0.0,
                          reset=True, eye=(2.0, 2.4, 0.6, 1.4, 0.75, 2.2, 8.0))
        self.assertAlmostEqual(respawned.position[0], -90.0 + 0.75 + 2.2 * 0.5, delta=1e-9)
        self.assertAlmostEqual(respawned.position[1], -4.0 + 2.6, delta=1e-9)
        self.assertAlmostEqual(respawned.position[2], 120.0, delta=1e-9)
        self.assertAlmostEqual(respawned.yaw_deg, 270.0, delta=1e-12)
        self.assertAlmostEqual(rig.height_m, 2.6, delta=1e-12)
        self.assertEqual(rig.frame, respawned)

        # The returned frame is the new baseline: feeding it back with dt = 0 must
        # reproduce it and not fall back to the pre-teleport frame.
        again = _step(rig, (-90.0, -4.0, 120.0), 0.5, 270.0, 0.0,
                      eye=(2.0, 2.4, 0.6, 1.4, 0.75, 2.2, 8.0))
        self.assertEqual(again, respawned)
        self.assertAlmostEqual(rig.height_m, 2.6, delta=1e-12)

    def test_rails_bound_the_constant_terms_not_the_radius_terms(self):
        # 100 Hz over 0.2 s gives a full-weight step, so the height lands on the target
        # immediately. The max rails bound the author-supplied CONSTANT part; the
        # min rail is a floor on the total height above the ball's CENTRE.
        rig = FollowRig(height_filter_hz=100.0, yaw_filter_hz=0.0)
        first = _step(rig, (0.0, 5.0, 0.0), 0.5, 0.0, 0.2,
                      eye=(100.0, 0.0, 0.5, 1.5, 0.75, 2.2, 8.0))
        self.assertAlmostEqual(first.position[1] - 5.0, 1.5, delta=1e-12)

        low = _step(rig, (0.0, 5.0, 0.0), 0.5, 0.0, 0.2,
                    eye=(0.1, 0.0, 0.5, 1.5, 0.75, 2.2, 8.0))
        self.assertAlmostEqual(low.position[1] - 5.0, 0.5, delta=1e-12)

        inside = _step(rig, (0.0, 5.0, 0.0), 0.5, 0.0, 0.2,
                       eye=(1.0, 0.0, 0.5, 1.5, 0.75, 2.2, 8.0))
        self.assertAlmostEqual(inside.position[1] - 5.0, 1.0, delta=1e-12)

        # The back rail bounds the constant too: base back 2.0 -> 1.0, so the offset is
        # 1.0 + 2.2 * 0.5 = 2.1 m behind the centre on -z at yaw 0 (not 3.1 m).
        clamped_back = FollowRig(height_filter_hz=0.0, yaw_filter_hz=0.0)
        frame = _step(clamped_back, (0.0, 0.0, 0.0), 0.5, 0.0, 0.0,
                      reset=True, eye=(0.55, 2.4, 0.1, 8.0, 2.0, 2.2, 1.0))
        self.assertAlmostEqual(frame.position[2], -(1.0 + 2.2 * 0.5), delta=1e-12)

        # A zero radius is allowed (a motor that has not derived a size yet reports it):
        # the same geometry with no ball, so 0.55 m up and 0.75 m back.
        zero = FollowRig(height_filter_hz=0.0, yaw_filter_hz=0.0)
        zero_frame = _step(zero, (0.0, 0.0, 0.0), 0.0, 0.0, 0.0, reset=True)
        self.assertAlmostEqual(zero.height_m, 0.55, delta=1e-12)
        self.assertAlmostEqual(zero_frame.position[2], -0.75, delta=1e-12)

        # 0.55 + 2.4 * 1.1 = 3.19 m above the centre: the radius term is not capped by a
        # 2.5 m max rail (base 0.55 <= 2.5), because that ceiling is not a view limit.
        large = FollowRig(height_filter_hz=0.0, yaw_filter_hz=0.0)
        _step(large, (0.0, 1.1, 0.0), 1.1, 0.0, 0.0,
              reset=True, eye=(0.55, 2.4, 0.1, 2.5, 0.75, 2.2, 8.0))
        self.assertAlmostEqual(large.height_m, 3.19, delta=1e-12)

    def test_the_eye_stays_outside_the_ball_at_the_global_cap(self):
        # The push stance: 1.654625 * r up and 1.176792 * r back, no constant. At the
        # global 400 m diameter cap (r = 200 m) a fixed 128 m rail would pin the frame
        # inside the ball; with the rails bounding only the constants it is at
        # (0, 330.925, 235.3584) from the centre, well outside a 200 m ball.
        stance = (0.0, 1.654625, 0.1, 128.0, 0.0, 1.176792, 128.0)
        rig = FollowRig(height_filter_hz=0.0, yaw_filter_hz=0.0)
        frame = _step(rig, (0.0, 200.0, 0.0), 200.0, 0.0, 0.0, reset=True, eye=stance)
        self.assertAlmostEqual(frame.position[1] - 200.0, 1.654625 * 200.0, delta=1e-6)
        self.assertAlmostEqual(-frame.position[2], 1.176792 * 200.0, delta=1e-6)
        offset = math.hypot(1.654625 * 200.0, 1.176792 * 200.0)
        self.assertGreater(offset, 200.0)
        self.assertGreater(frame.position[1] - 200.0, 200.0)

        # The motor's own defaults (constant 0.55 up / 0.75 back, 2.4 / 2.2 per radius)
        # are outside the ball at the same size as well.
        motor = FollowRig(height_filter_hz=0.0, yaw_filter_hz=0.0)
        motor_frame = _step(motor, (0.0, 200.0, 0.0), 200.0, 0.0, 0.0, reset=True)
        up = motor_frame.position[1] - 200.0
        back = -motor_frame.position[2]
        self.assertAlmostEqual(up, 0.55 + 2.4 * 200.0, delta=1e-6)
        self.assertAlmostEqual(back, 0.75 + 2.2 * 200.0, delta=1e-6)
        self.assertGreater(math.hypot(up, back), 200.0)

    def test_camera_is_behind_and_above_and_both_grow_with_the_ball(self):
        # The four judged diameters: height = 0.55 + 2.4 * r, back = 0.75 + 2.2 * r, from
        # r = d / 2. A snap (reset) keeps this a pure model check: no filter lag.
        diameters = (0.10, 0.30, 0.72, 1.43)
        expected_height = (0.67, 0.91, 1.414, 2.266)
        expected_back = (0.86, 1.08, 1.542, 2.323)
        previous_height = None
        previous_back = None
        previous_gap = None
        frames = []

        for index, diameter in enumerate(diameters):
            radius = diameter * 0.5
            rig = FollowRig(height_filter_hz=0.0, yaw_filter_hz=0.0)
            frame = _step(rig, (0.0, radius, 0.0), radius, 0.0, 0.0, reset=True)
            frames.append(frame)

            self.assertAlmostEqual(frame.position[1] - radius, expected_height[index],
                                   delta=1e-9, msg=f"height above the centre at d={diameter}")
            self.assertAlmostEqual(-frame.position[2], expected_back[index],
                                   delta=1e-9, msg=f"back offset at d={diameter}")
            self.assertAlmostEqual(frame.position[0], 0.0, delta=1e-12)
            self.assertAlmostEqual(rig.height_m, expected_height[index], delta=1e-9)

            # The property the old fixed height lost: the clearance above the SURFACE
            # (height - radius) must not shrink as the ball grows.
            gap = frame.position[1] - 2.0 * radius
            if previous_gap is not None:
                self.assertGreater(gap, previous_gap)
                self.assertGreater(frame.position[1] - radius, previous_height)
                self.assertGreater(expected_back[index], previous_back)
            previous_height = frame.position[1] - radius
            previous_back = expected_back[index]
            previous_gap = gap

        # The 0.10 m ball is played from just above the floor: 0.67 m above the centre
        # is 0.72 m above the floor for a grounded ball and 0.62 m above its top - not a
        # standing person's eye height, and not hugging the floor either.
        small_height = frames[0].position[1] - 0.05
        self.assertLess(small_height, 0.9)
        self.assertGreater(frames[0].position[1], 0.5)
        self.assertGreater(small_height - 0.05, 0.0)

    def test_interface_matches_the_csharp_signature(self):
        # try_update mirrors FollowRigMath.TryUpdate argument order, including the
        # caller-owned previous frame.
        self.assertEqual(
            tuple(inspect.signature(try_update).parameters),
            ("height_filter_hz", "yaw_filter_hz", "ball_position", "ball_radius_m",
             "horizontal_yaw_deg", "dt_s", "eye_base_up_m", "eye_up_per_radius",
             "eye_up_min_m", "eye_up_max_m", "eye_base_back_m", "eye_back_per_radius",
             "eye_max_back_m", "reset_to_teleport", "has_previous_frame",
             "previous_rig_yaw_deg", "previous_height_m"))
        self.assertEqual(
            tuple(inspect.signature(FollowRig.update).parameters),
            ("self", "ball_position", "ball_radius_m", "horizontal_yaw_deg", "dt_s",
             "eye_base_up_m", "eye_up_per_radius", "eye_up_min_m", "eye_up_max_m",
             "eye_base_back_m", "eye_back_per_radius", "eye_max_back_m", "reset"))
        frame = _step(FollowRig(), (0.0, 1.0, 0.0), 0.15, 123.0, 0.0)
        self.assertFalse(hasattr(frame, "roll_deg"))
        self.assertFalse(hasattr(frame, "pitch_deg"))

    def test_invalid_inputs_are_rejected_atomically(self):
        rig = FollowRig()
        old = _step(rig, (1.0, 2.0, 3.0), 0.15, 45.0, 0.1)
        old_height = rig.height_m

        def eye_with(**changes):
            values = list(MOTOR_EYE)
            names = ("eye_base_up_m", "eye_up_per_radius", "eye_up_min_m", "eye_up_max_m",
                     "eye_base_back_m", "eye_back_per_radius", "eye_max_back_m")
            for name, value in changes.items():
                values[names.index(name)] = value
            return tuple(values)

        bad_calls = (
            # ball position / radius
            ((float("nan"), 0.0, 0.0), 0.15, 0.0, 0.1, MOTOR_EYE),
            ((0.0, 0.0, 0.0), float("nan"), 0.0, 0.1, MOTOR_EYE),
            ((0.0, 0.0, 0.0), -0.15, 0.0, 0.1, MOTOR_EYE),
            ((0.0, 0.0, 0.0), float("inf"), 0.0, 0.1, MOTOR_EYE),
            # heading / time
            ((0.0, 0.0, 0.0), 0.15, float("inf"), 0.1, MOTOR_EYE),
            ((0.0, 0.0, 0.0), 0.15, float("nan"), 0.1, MOTOR_EYE),
            ((0.0, 0.0, 0.0), 0.15, 0.0, -0.1, MOTOR_EYE),
            ((0.0, 0.0, 0.0), 0.15, 0.0, float("nan"), MOTOR_EYE),
            # eye geometry and rails
            ((0.0, 0.0, 0.0), 0.15, 0.0, 0.1, eye_with(eye_base_up_m=float("nan"))),
            ((0.0, 0.0, 0.0), 0.15, 0.0, 0.1, eye_with(eye_base_up_m=-0.55)),
            ((0.0, 0.0, 0.0), 0.15, 0.0, 0.1, eye_with(eye_up_per_radius=-2.4)),
            ((0.0, 0.0, 0.0), 0.15, 0.0, 0.1, eye_with(eye_up_min_m=8.0, eye_up_max_m=0.1)),
            ((0.0, 0.0, 0.0), 0.15, 0.0, 0.1, eye_with(eye_up_min_m=-0.1)),
            ((0.0, 0.0, 0.0), 0.15, 0.0, 0.1, eye_with(eye_up_max_m=-1.0)),
            ((0.0, 0.0, 0.0), 0.15, 0.0, 0.1, eye_with(eye_base_back_m=-0.75)),
            ((0.0, 0.0, 0.0), 0.15, 0.0, 0.1, eye_with(eye_back_per_radius=-2.2)),
            ((0.0, 0.0, 0.0), 0.15, 0.0, 0.1, eye_with(eye_max_back_m=-8.0)),
        )
        for ball, radius, yaw, dt, eye in bad_calls:
            with self.subTest(ball=ball, radius=radius, yaw=yaw, dt=dt, eye=eye):
                with self.assertRaises(ValueError):
                    rig.update(ball, radius, yaw, dt, *eye)
                self.assertEqual(rig.frame, old)
                self.assertEqual(rig.height_m, old_height)

        with self.assertRaises(ValueError):
            _step(rig, (0.0, 0.0, 0.0), 0.15, 0.0, 0.1, reset=1)
        self.assertEqual(rig.frame, old)
        self.assertEqual(rig.height_m, old_height)

    def test_huge_filter_product_is_rejected_without_partial_commit(self):
        rig = FollowRig(height_filter_hz=1e308, yaw_filter_hz=1.0)
        old = _step(rig, (0.0, 0.0, 0.0), 0.15, 0.0, 0.0)
        old_height = rig.height_m
        # 2*pi*1e308 overflows a double to infinity, so the call is refused instead of
        # committing a candidate frame. (C# hits this same rejection path at ~3.4e38
        # because its fields are float32; the Python twin only overflows at double's
        # ~1.8e308 - the documented precision-domain difference, not a semantic one.)
        with self.assertRaises(OverflowError):
            _step(rig, (10.0, 0.0, 0.0), 0.15, 90.0, 1e308)
        self.assertEqual(rig.frame, old)
        self.assertEqual(rig.height_m, old_height)

        # An infinite geometry term is refused the same way: a 1e308 m radius makes the
        # back offset overflow before any candidate is committed.
        with self.assertRaises(OverflowError):
            _step(rig, (10.0, 0.0, 0.0), 1e308, 90.0, 0.1)
        self.assertEqual(rig.frame, old)
        self.assertEqual(rig.height_m, old_height)

    def test_deterministic_long_sequence_remains_finite_and_height_limited(self):
        rig = FollowRig(height_filter_hz=4.0, yaw_filter_hz=6.0)
        for i in range(1000):
            x = math.sin(i * 0.013) * 50.0
            z = math.cos(i * 0.017) * 30.0
            yaw = (359.0 + i * 0.7) % 360.0
            ball_y = float(i % 9) - 4.0
            radius = 0.05 + (i % 7) * 0.1
            had_frame = rig.frame is not None
            previous_yaw = rig.frame.yaw_deg if rig.frame else 0.0

            frame = _step(rig, (x, ball_y, z), radius, yaw, 1.0 / 90.0)
            self.assertTrue(all(math.isfinite(value) for value in frame.position))
            self.assertTrue(math.isfinite(frame.yaw_deg))
            self.assertGreaterEqual(rig.height_m, MOTOR_EYE[2] - 1e-12)
            self.assertLessEqual(rig.height_m, MOTOR_EYE[3] + 1e-12)
            self.assertAlmostEqual(frame.position[1], ball_y + rig.height_m, delta=1e-9)
            dx, dz = frame.position[0] - x, frame.position[2] - z
            yaw_rad = math.radians(frame.yaw_deg)
            self.assertAlmostEqual(dx * math.cos(yaw_rad) - dz * math.sin(yaw_rad), 0.0, delta=1e-12)
            self.assertAlmostEqual(dx * math.sin(yaw_rad) + dz * math.cos(yaw_rad),
                                   -(MOTOR_EYE[4] + radius * MOTOR_EYE[5]), delta=1e-12)

            if had_frame:
                # A filtered yaw approaches its target monotonically: the remaining
                # short-arc distance cannot grow.
                before = abs(_wrap_shortest(yaw, previous_yaw))
                after = abs(_wrap_shortest(yaw, frame.yaw_deg))
                self.assertLessEqual(after, before + 1e-3)


if __name__ == "__main__":
    unittest.main()
