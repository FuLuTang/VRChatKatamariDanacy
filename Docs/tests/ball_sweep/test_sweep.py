import math
import unittest

from Docs.Design.BallLayers.Sweep.sweep import Box, Plane, sweep_sphere


class SweepTests(unittest.TestCase):
    def test_high_speed_does_not_cross_thin_wall(self):
        wall = Box((0.0, -2.0, -2.0), (0.1, 2.0, 2.0))
        result = sweep_sphere((-20.0, 0.0, 0.0), (100.0, 0.0, 0.0), 0.5, (wall,))
        self.assertAlmostEqual(result.position[0], -0.5, places=7)
        self.assertTrue(result.contacts)
        self.assertLessEqual(result.position[0], wall.minimum[0] - 0.5 + 1e-7)
        self.assertTrue(result.blocked)
        self.assertFalse(result.stuck)

    def test_wall_thinner_than_radius_is_not_tunnelled(self):
        wall = Box((5.0, -1.0, -1.0), (5.05, 1.0, 1.0))
        result = sweep_sphere((0.0, 0.0, 0.0), (12.0, 0.0, 0.0), 1.0, (wall,))
        self.assertAlmostEqual(result.position[0], 4.0, places=7)
        self.assertAlmostEqual(result.contacts[0].normal[0], -1.0)

    def test_corner_projects_and_slides_out(self):
        walls = (Plane((1.0, 0.0, 0.0)), Plane((0.0, 1.0, 0.0)))
        result = sweep_sphere((1.0, 1.0, 0.0), (-2.0, -2.0, 3.0), 0.0, walls)
        self.assertGreaterEqual(result.position[0], -1e-7)
        self.assertGreaterEqual(result.position[1], -1e-7)
        self.assertAlmostEqual(result.position[2], 3.0)
        self.assertFalse(result.stuck)
        self.assertFalse(result.blocked)

    def test_multiple_contacts_have_analytically_valid_order_and_time(self):
        walls = (Plane((1.0, 0.0, 0.0)), Plane((0.0, 1.0, 0.0)))
        # Both axes reach their planes: y first at t=1/2, then x at t=2/3.
        result = sweep_sphere((2.0, 2.0, 0.0), (-3.0, -4.0, 0.0), 0.0, walls)
        self.assertEqual([contact.obstacle_index for contact in result.contacts], [1, 0])
        self.assertAlmostEqual(result.contacts[0].time, 0.5)
        self.assertAlmostEqual(result.contacts[1].time, 2.0 / 3.0)
        self.assertFalse(result.stuck)

    def test_full_single_wall_block_is_blocked_not_stuck(self):
        wall = Plane((1.0, 0.0, 0.0))
        result = sweep_sphere((2.0, 0.0, 0.0), (-3.0, 0.0, 0.0), 0.5, (wall,))
        self.assertAlmostEqual(result.position[0], 0.5, places=7)
        self.assertEqual(result.remaining_displacement, (0.0, 0.0, 0.0))
        self.assertTrue(result.blocked)
        self.assertFalse(result.stuck)

    def test_zero_velocity_and_tangent_start_do_not_jitter(self):
        wall = Plane((1.0, 0.0, 0.0))
        stationary = sweep_sphere((1.0, 0.0, 0.0), (0.0, 0.0, 0.0), 1.0, (wall,))
        self.assertEqual(stationary.position, (1.0, 0.0, 0.0))
        self.assertEqual(stationary.contacts, ())
        tangent = sweep_sphere((1.0, 0.0, 0.0), (0.0, 0.0, 2.0), 1.0, (wall,))
        self.assertAlmostEqual(tangent.position[2], 2.0)
        self.assertEqual(tangent.contacts, ())
        self.assertFalse(tangent.stuck)

    def test_zero_displacement_initial_plane_embedding_is_recovered(self):
        result = sweep_sphere((0.25, 0.0, 0.0), (0.0, 0.0, 0.0), 0.5,
                              (Plane((1.0, 0.0, 0.0)),))
        self.assertAlmostEqual(result.contacts[0].penetration_depth, 0.25)
        self.assertGreaterEqual(result.position[0], 0.5)
        self.assertGreater(result.position[0] - 0.5, 0.0)
        self.assertFalse(result.stuck)
        self.assertFalse(result.blocked)

    def test_box_initial_embedding_uses_real_expanded_face_depth(self):
        box = Box((-1.0, -1.0, -1.0), (1.0, 1.0, 1.0))
        result = sweep_sphere((0.0, 0.0, 0.0), (0.0, 0.0, 0.0), 0.5, (box,))
        self.assertAlmostEqual(result.contacts[0].penetration_depth, 1.5)
        self.assertLessEqual(result.position[0], -1.5)
        self.assertAlmostEqual(result.position[1], 0.0)
        self.assertFalse(result.stuck)

    def test_box_asymmetric_embedding_pushes_to_nearest_free_face(self):
        box = Box((-1.0, -1.0, -1.0), (1.0, 1.0, 1.0))
        result = sweep_sphere((0.25, 0.8, 0.0), (0.0, 0.0, 0.0), 0.5, (box,))
        self.assertAlmostEqual(result.contacts[0].penetration_depth, 0.7)
        self.assertGreaterEqual(result.position[1], 1.5)
        self.assertFalse(result.stuck)

    def test_expanded_aabb_corner_is_conservative_not_exact_sphere_box(self):
        box = Box((0.0, 0.0, 0.0), (1.0, 1.0, 1.0))
        start = (-0.4, -0.4, 0.5)
        exact_nearest_distance = math.hypot(0.4, 0.4)
        self.assertGreater(exact_nearest_distance, 0.5)
        result = sweep_sphere(start, (0.0, 0.0, 0.0), 0.5, (box,))
        self.assertTrue(result.contacts)  # expected square-expansion false positive
        self.assertFalse(result.stuck)

    def test_contact_budget_exhaustion_is_explicit_and_keeps_residual(self):
        walls = (Plane((1.0, 0.0, 0.0)), Plane((0.0, 1.0, 0.0)))
        result = sweep_sphere((2.0, 2.0, 0.0), (-3.0, -4.0, 0.0), 0.0,
                              walls, max_contacts=1)
        self.assertTrue(result.stuck)
        self.assertGreater(math.hypot(*result.remaining_displacement), 0.0)

    def test_same_inputs_are_deterministic(self):
        obstacles = (Plane((1.0, 0.0, 0.0)),
                     Box((0.0, 3.0, -1.0), (0.2, 4.0, 1.0)))
        a = sweep_sphere((2.0, 2.0, 0.0), (-4.0, 3.0, 0.0), 0.25, obstacles)
        b = sweep_sphere((2.0, 2.0, 0.0), (-4.0, 3.0, 0.0), 0.25, obstacles)
        self.assertEqual(a, b)

    def test_nonunit_plane_scales_its_offset_together(self):
        result = sweep_sphere((0.0, 0.0, 0.0), (0.0, 0.0, 0.0), 0.5,
                              (Plane((0.0, 2.0, 0.0), 4.0),))
        self.assertAlmostEqual(result.position[1], 2.5)

    def test_invalid_or_nonfinite_geometry_is_rejected(self):
        with self.assertRaises(ValueError):
            sweep_sphere((0.0, 0.0), (1.0, 0.0, 0.0), 1.0, ())
        with self.assertRaises(ValueError):
            sweep_sphere((0.0, 0.0, 0.0), (0.0, 0.0, 0.0), -1.0, ())
        with self.assertRaises(ValueError):
            sweep_sphere((0.0, 0.0, 0.0), (0.0, 0.0, 0.0), 1.0,
                         (Box((1.0, 0.0, 0.0), (0.0, 1.0, 1.0)),))
        with self.assertRaises(ValueError):
            sweep_sphere((0.0, 0.0, 0.0), (0.0, 0.0, 0.0), 1.0,
                         (Plane((0.0, 0.0, 0.0)),))
        with self.assertRaises(ValueError):
            sweep_sphere((1e308, 0.0, 0.0), (1e308, 0.0, 0.0), 1.0, ())
        with self.assertRaises(ValueError):
            sweep_sphere((1e308, 0.0, 0.0), (-1.1e-10, 0.0, 0.0), 0.0,
                         (Box((-1.0, -1.0, -1.0), (1.0, 1.0, 1.0)),))


if __name__ == "__main__":
    unittest.main()
