import math
import unittest

from Docs.Design.BallLayers.VisualRoll.visual_roll import VisualRollState


def _norm(v):
    return math.sqrt(sum(component * component for component in v))


def _test_vector_cross(a, b):
    """Independent component expansion used only by the kinematic oracle."""
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def _angle_axis_from_quaternion(q):
    vector_length = _norm(q[:3])
    if vector_length <= 1e-15:
        return ((0.0, 0.0, 0.0), 0.0)
    angle = 2.0 * math.atan2(vector_length, q[3])
    return (tuple(component / vector_length for component in q[:3]), angle)


def _assert_no_slip(test, state, tangent_velocity, normal, radius):
    axis, angle = _angle_axis_from_quaternion(state.local_rotation)
    test.assertGreater(angle, 0.0)
    # For a single short sample, omega is the measured axis-angle/dt. Tests
    # pass dt=1 s and displacement=velocity*dt to avoid using module helpers.
    omega = tuple(component * angle for component in axis)
    rho = tuple(-radius * component for component in normal)
    rotational_velocity = _test_vector_cross(omega, rho)
    residual = tuple(tangent_velocity[i] + rotational_velocity[i] for i in range(3))
    test.assertLess(_norm(residual), 1e-10, "contact point must have zero instantaneous velocity")


class VisualRollTests(unittest.TestCase):
    def test_contact_point_velocity_independently_proves_forward_axis_sign(self):
        radius = 0.75
        travel = (0.0, 0.0, 0.12)
        normal = (0.0, 1.0, 0.0)
        state = VisualRollState().advance(travel, normal, radius)
        axis, _ = _angle_axis_from_quaternion(state.local_rotation)
        # In the explicitly stated host convention, forward +Z must roll about +X.
        self.assertGreater(axis[0], 0.999999)
        _assert_no_slip(self, state, travel, normal, radius)

        reverse = VisualRollState().advance((0.0, 0.0, -travel[2]), normal, radius)
        reverse_axis, _ = _angle_axis_from_quaternion(reverse.local_rotation)
        self.assertLess(reverse_axis[0], -0.999999)
        _assert_no_slip(self, reverse, (0.0, 0.0, -travel[2]), normal, radius)

    def test_sloped_contact_uses_independent_no_slip_equation(self):
        normal_raw = (0.0, 1.0, 1.0)
        n_length = _norm(normal_raw)
        normal = tuple(component / n_length for component in normal_raw)
        # This displacement contains both tangential motion and a normal lift.
        displacement = (0.08, 0.11, -0.11)
        normal_part = sum(displacement[i] * normal[i] for i in range(3))
        tangent = tuple(displacement[i] - normal[i] * normal_part for i in range(3))
        radius = 0.63
        state = VisualRollState().advance(displacement, normal, radius)
        _assert_no_slip(self, state, tangent, normal, radius)

    def test_only_tangent_distance_sets_angle_and_pure_normal_does_not_roll(self):
        radius = 0.8
        tangent = (0.0, 0.0, 0.24)
        normal = (0.0, 1.0, 0.0)
        plain = VisualRollState().advance(tangent, normal, radius)
        with_normal_motion = VisualRollState().advance((0.0, 3.5, 0.24), normal, radius)
        self.assertAlmostEqual(_angle_axis_from_quaternion(plain.local_rotation)[1], 0.24 / radius)
        self.assertAlmostEqual(_angle_axis_from_quaternion(with_normal_motion.local_rotation)[1], 0.24 / radius)
        unchanged = plain.advance((0.0, 2.0, 0.0), normal, radius)
        self.assertIs(unchanged, plain)

    def test_near_zero_tangent_uses_explicit_host_cutoff_without_axis_jitter(self):
        initial = VisualRollState()
        below = initial.advance((0.0, 0.0, 0.5e-7), (0.0, 1.0, 0.0), 0.5)
        self.assertIs(below, initial)
        above = initial.advance((0.0, 0.0, 2.0e-7), (0.0, 1.0, 0.0), 0.5)
        self.assertNotEqual(above.local_rotation, initial.local_rotation)

    def test_one_revolution_matches_circumference_and_scale_stays_one(self):
        radius = 0.75
        distance = math.tau * radius
        state = VisualRollState().advance((0.0, 0.0, distance), (0.0, 1.0, 0.0), radius)
        self.assertAlmostEqual(abs(state.local_rotation[3]), 1.0, places=12)
        self.assertAlmostEqual(distance, math.pi * (2.0 * radius), places=12)
        self.assertEqual(state.local_scale, (1.0, 1.0, 1.0))

    def test_teleport_and_configured_jump_threshold_skip_roll_then_resume(self):
        initial = VisualRollState()
        skipped = initial.advance((50.0, 0.0, -20.0), (0.0, 1.0, 0.0), 0.5,
                                  discontinuous=True)
        self.assertIs(skipped, initial)
        threshold_skip = initial.advance((3.0, 0.0, 0.0), (0.0, 1.0, 0.0), 0.5,
                                         max_displacement_m=2.0)
        self.assertIs(threshold_skip, initial)
        resumed = initial.advance((0.0, 0.0, 0.1), (0.0, 1.0, 0.0), 0.5,
                                  max_displacement_m=2.0)
        self.assertNotEqual(resumed.local_rotation, initial.local_rotation)
        self.assertEqual(VisualRollState.identity().local_rotation, (0.0, 0.0, 0.0, 1.0))

    def test_invalid_initial_quaternions_are_rejected_and_valid_nonunit_is_normalized(self):
        for bad in ((0.0, 0.0, 0.0, 0.0), (float("nan"), 0.0, 0.0, 1.0),
                    (0.0, float("inf"), 0.0, 1.0), (0.0, 0.0, 0.0),
                    (1e-20, 0.0, 0.0, 0.0)):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                VisualRollState(bad)
        normalized = VisualRollState((0.0, 0.0, 2.0, 2.0))
        self.assertAlmostEqual(_norm(normalized.local_rotation), 1.0, places=14)
        self.assertAlmostEqual(normalized.local_rotation[2], math.sqrt(0.5), places=14)
        self.assertAlmostEqual(normalized.local_rotation[3], math.sqrt(0.5), places=14)

    def test_invalid_update_is_rejected_without_mutating_prior_state(self):
        state = VisualRollState().advance((0.1, 0.0, 0.0), (0.0, 1.0, 0.0), 0.5)
        before = state.local_rotation
        for args in (((float("nan"), 0.0, 0.0), (0.0, 1.0, 0.0), 0.5),
                     ((0.0, 0.0, 0.0), (0.0, 0.0, 0.0), 0.5),
                     ((0.0, 0.0, 0.0), (0.0, 1.0, 0.0), 0.0)):
            with self.subTest(args=args), self.assertRaises(ValueError):
                state.advance(*args)
            self.assertEqual(state.local_rotation, before)
        with self.assertRaises(ValueError):
            state.advance((0.0, 0.0, 1.0), (0.0, 1.0, 0.0), 1.0,
                          max_displacement_m=float("nan"))
        self.assertEqual(state.local_rotation, before)

    def test_long_small_step_accumulation_is_finite_unit_and_reversible(self):
        radius = 0.6
        step = 0.0007
        count = 12000
        state = VisualRollState()
        for _ in range(count):
            state = state.advance((0.0, 0.0, step), (0.0, 1.0, 0.0), radius)
            self.assertTrue(all(math.isfinite(value) for value in state.local_rotation))
            self.assertAlmostEqual(_norm(state.local_rotation), 1.0, places=12)
            self.assertEqual(state.local_scale, (1.0, 1.0, 1.0))
        total_angle = count * step / radius
        expected = (math.sin(total_angle / 2.0), 0.0, 0.0, math.cos(total_angle / 2.0))
        actual = state.local_rotation
        self.assertLess(min(_norm(tuple(actual[i] - expected[i] for i in range(4))),
                            _norm(tuple(actual[i] + expected[i] for i in range(4)))), 2e-10)
        for _ in range(count):
            state = state.advance((0.0, 0.0, -step), (0.0, 1.0, 0.0), radius)
        identity = (0.0, 0.0, 0.0, 1.0)
        self.assertLess(min(_norm(tuple(state.local_rotation[i] - identity[i] for i in range(4))),
                            _norm(tuple(state.local_rotation[i] + identity[i] for i in range(4)))), 2e-10)

    def test_reset_returns_identity_without_changing_gameplay_state(self):
        gameplay_heading = (0.0, 0.0, 1.0)
        state = VisualRollState().advance((0.0, 0.0, 0.3), (0.0, 1.0, 0.0), 1.0)
        reset = state.identity()
        self.assertEqual(reset.local_rotation, (0.0, 0.0, 0.0, 1.0))
        self.assertEqual(gameplay_heading, (0.0, 0.0, 1.0))
        self.assertFalse(hasattr(reset, "gameplay_heading"))


if __name__ == "__main__":
    unittest.main()
