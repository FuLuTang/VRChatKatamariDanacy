import math
import unittest

from Docs.Design.BallLayers.GroundContact.ground_contact import (
    rotation_matrix_from_quaternion,
    solve_ground_contact,
)

IDENTITY = ((1., 0., 0.), (0., 1., 0.), (0., 0., 1.))


def solve(radius=2., points=(), normal=(0., 1., 0.), offset=0., **kwargs):
    args = dict(current_center=(0., 0., 0.), max_lift_per_step=100.,
                max_drop_per_step=100., requested_normal_speed_perturbation=0.,
                max_normal_speed_perturbation=100.)
    args.update(kwargs)
    return solve_ground_contact(radius, IDENTITY, points, normal, offset, **args)


class GroundContactTests(unittest.TestCase):
    def test_empty_proxy_reduces_to_sphere(self):
        result = solve()
        self.assertEqual(result.support_height, 2.)
        self.assertEqual(result.center_position, (0., 2., 0.))
        self.assertEqual(result.center_normal_coordinate, 2.)
        self.assertEqual(result.plane_anchor, (0., 0., 0.))

    def test_proxy_extension_raises_support(self):
        result = solve(points=((0., -3.5, 0.),))
        self.assertEqual(result.support_height, 3.5)
        self.assertEqual(result.center_position, (0., 3.5, 0.))

    def test_relative_extrusion_shrinks_as_core_grows(self):
        small = solve(radius=1., points=((0., -3.5, 0.),))
        large = solve(radius=3., points=((0., -3.5, 0.),))
        self.assertEqual(small.support_height - 1., 2.5)
        self.assertEqual(large.support_height - 3., .5)

    def test_upward_correction_is_capped(self):
        result = solve(points=((0., -5., 0.),), max_lift_per_step=1.25)
        self.assertEqual(result.support_height, 5.)
        self.assertEqual(result.center_position, (0., 1.25, 0.))
        self.assertTrue(result.lift_clamped)
        self.assertFalse(result.drop_clamped)

    def test_detached_support_can_fall_with_drop_cap(self):
        result = solve(radius=1., current_center=(2., 3.5, 4.),
                       max_drop_per_step=.4)
        self.assertEqual(result.support_height, 1.)
        self.assertEqual(result.center_position, (2., 3.1, 4.))
        self.assertEqual(result.applied_normal_correction, -.4)
        self.assertTrue(result.drop_clamped)

    def test_detached_support_eventually_reaches_current_geometry(self):
        result = solve(radius=1., current_center=(2., 3.5, 4.),
                       max_drop_per_step=10.)
        self.assertEqual(result.center_position, (2., 1., 4.))
        self.assertFalse(result.drop_clamped)

    def test_speed_perturbation_is_symmetric_and_capped(self):
        result = solve(requested_normal_speed_perturbation=-9.,
                       max_normal_speed_perturbation=2.)
        self.assertEqual(result.normal_speed_perturbation, -2.)

    def test_raw_normal_and_plane_offset_are_normalized_together(self):
        result = solve(radius=1., normal=(0., 2., 0.), offset=4.)
        self.assertEqual(result.center_position, (0., 3., 0.))
        self.assertEqual(result.plane_anchor, (0., 2., 0.))

    def test_slope_correction_is_along_normal_and_preserves_tangent(self):
        normal = (1., 1., 0.)
        start = (5., -5., 7.)  # dot(normalized normal, start) == 0
        result = solve(radius=.5, points=((-2., -2., 0.),), normal=normal,
                       current_center=start)
        target = 4. / math.sqrt(2.)
        self.assertAlmostEqual(result.support_height, target)
        self.assertAlmostEqual(result.center_normal_coordinate, target)
        self.assertAlmostEqual(result.center_position[0] - result.center_position[1], 10.)
        self.assertAlmostEqual(result.center_position[2], 7.)
        self.assertAlmostEqual(result.center_position[0], 5. + target / math.sqrt(2.))
        self.assertAlmostEqual(result.center_position[1], -5. + target / math.sqrt(2.))
        self.assertEqual(result.plane_anchor, (0., 0., 0.))

    def test_plane_anchor_is_not_claimed_as_proxy_contact_point(self):
        result = solve(radius=1., normal=(0., 2., 0.), offset=4.)
        self.assertEqual(result.plane_anchor, (0., 2., 0.))
        self.assertNotIn("contact_point", result.__dataclass_fields__)

    def test_quaternion_adapter_has_explicit_host_direction_probe(self):
        half = math.pi / 4.
        matrix = rotation_matrix_from_quaternion((0., math.sin(half), 0., math.cos(half)))
        forward = tuple(sum(matrix[row][column] * (0., 0., 1.)[column]
                            for column in range(3)) for row in range(3))
        # Host Hamilton probe only: this is NOT Unity 2022.3 parity evidence.
        self.assertAlmostEqual(forward[0], 1.)
        self.assertAlmostEqual(forward[1], 0.)
        self.assertAlmostEqual(forward[2], 0.)

    def test_invalid_rotation_shape_scale_and_reflection_are_rejected(self):
        invalid_matrices = (
            ((1., 0.), (0., 1.), (0., 0.)),
            ((2., 0., 0.), (0., 1., 0.), (0., 0., 1.)),
            ((-1., 0., 0.), (0., 1., 0.), (0., 0., 1.)),
            ((float("nan"), 0., 0.), (0., 1., 0.), (0., 0., 1.)),
        )
        for matrix in invalid_matrices:
            with self.subTest(matrix=matrix), self.assertRaises(ValueError):
                solve_ground_contact(1., matrix, (), (0., 1., 0.), 0.,
                                     current_center=(0., 0., 0.),
                                     max_lift_per_step=1., max_drop_per_step=1.,
                                     requested_normal_speed_perturbation=0.,
                                     max_normal_speed_perturbation=1.)

    def test_bad_geometry_and_negative_caps_are_rejected(self):
        with self.assertRaises(ValueError):
            solve(normal=(0., 0., 0.))
        with self.assertRaises(ValueError):
            solve(points=((0., float("inf"), 0.),))
        with self.assertRaises(ValueError):
            solve(max_drop_per_step=-.1)
        with self.assertRaises(ValueError):
            rotation_matrix_from_quaternion((0., 0., 0., 0.))

    def test_non_finite_intermediate_is_rejected(self):
        with self.assertRaises(ValueError):
            solve(radius=1., points=((-1.1e308, -1.1e308, -1.1e308),),
                  normal=(1., 1., 1.))


if __name__ == "__main__":
    unittest.main()
